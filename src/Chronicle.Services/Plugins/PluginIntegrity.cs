using System.Security.Cryptography;
using System.Text;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Chronicle.Services.Plugins;

/// <summary>A plugin was refused: not on the allowed list, or its files changed behind Chronicle's back.</summary>
public sealed class PluginNotAllowedException(string message) : Exception(message);

public enum IntegrityOutcome
{
    /// <summary>The files match what was recorded.</summary>
    Verified,
    /// <summary>No hash was on record (a plugin from before hashing); the current files were recorded.</summary>
    Recorded,
    /// <summary>The files differ from what was recorded. The plugin must not be loaded.</summary>
    Blocked,
}

public interface IPluginIntegrity
{
    /// <summary>The folder plugins live in (a plugin to be installed must be inside it).</summary>
    string PluginsDirectory { get; }

    /// <summary>True when <paramref name="path"/> is inside <see cref="PluginsDirectory"/>.</summary>
    bool IsInsidePluginsDirectory(string path);

    /// <summary>Whether a plugin id may be installed: it is in Chronicle's catalog, or an administrator has switched
    /// on <c>plugins.allow_unlisted</c>.</summary>
    Task<bool> IsAllowedAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Compares the plugin's files with the recorded hash. A mismatch marks the plugin blocked and tells
    /// administrators; it does not change the recorded hash.</summary>
    Task<IntegrityOutcome> VerifyAsync(Plugin plugin, CancellationToken ct = default);

    /// <summary>Records the plugin's current files as the trusted ones (after an install, an update through Chronicle,
    /// or an administrator's explicit approval), remembering the previous hash.</summary>
    Task AcceptCurrentFilesAsync(Plugin plugin, CancellationToken ct = default);
}

public sealed class PluginIntegrity : IPluginIntegrity
{
    public const string AllowUnlistedKey = "plugins.allow_unlisted";

    private readonly ChronicleDbContext _db;
    private readonly INotificationService? _notifications;
    private readonly IPluginCatalogSource? _catalog;
    private readonly ILogger _log = Log.ForContext<PluginIntegrity>();

    public string PluginsDirectory { get; }

    public PluginIntegrity(ChronicleDbContext db, IHostEnvironment env, INotificationService? notifications = null,
        IPluginCatalogSource? catalog = null)
    {
        _db = db;
        _notifications = notifications;
        _catalog = catalog;
        PluginsDirectory = Path.GetFullPath(Path.Combine(env.ContentRootPath, "plugins"));
    }

    public bool IsInsidePluginsDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = PluginsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> IsAllowedAsync(string pluginId, CancellationToken ct = default)
    {
        // "In the catalog" means in the catalog as hosted (or its last good copy, or the built-in list), not a fixed list.
        var listed = _catalog is null ? PluginCatalogSeeds.Entries : (await _catalog.GetAsync(ct)).Seeds;
        if (listed.Any(e => string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)))
            return true;
        var setting = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == AllowUnlistedKey, ct);
        return string.Equals(setting?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One hash for the whole plugin: every DLL under its folder plus manifest.json, by relative name and
    /// content, in a fixed order. A changed, added or removed file changes it.</summary>
    public static string ComputeFilesHash(string dllPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dllPath))
                  ?? throw new ArgumentException("The plugin path has no folder.", nameof(dllPath));
        var files = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories).ToList();
        var manifest = Path.Combine(dir, "manifest.json");
        if (File.Exists(manifest)) files.Add(manifest);

        using var total = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(f => Path.GetRelativePath(dir, f), StringComparer.OrdinalIgnoreCase))
        {
            total.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(dir, file).Replace('\\', '/').ToLowerInvariant()));
            total.AppendData([0]);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            total.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(total.GetHashAndReset()).ToLowerInvariant();
    }

    public async Task<IntegrityOutcome> VerifyAsync(Plugin plugin, CancellationToken ct = default)
    {
        // Nothing on disk to check: the loader reports a missing DLL in its own, clearer way.
        if (!File.Exists(plugin.DllPath)) return IntegrityOutcome.Verified;
        var actual = ComputeFilesHash(plugin.DllPath);

        if (string.IsNullOrEmpty(plugin.FilesSha256))
        {
            plugin.FilesSha256 = actual;
            plugin.FilesChangedAt ??= DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.Information("Plugin {PluginId}: no file hash was on record; recorded {Hash}", plugin.PluginId, actual[..12]);
            return IntegrityOutcome.Recorded;
        }

        if (string.Equals(plugin.FilesSha256, actual, StringComparison.OrdinalIgnoreCase))
        {
            if (plugin.IntegrityBlockedAt is not null)
            {
                plugin.IntegrityBlockedAt = null;
                await _db.SaveChangesAsync(ct);
            }
            return IntegrityOutcome.Verified;
        }

        var firstTime = plugin.IntegrityBlockedAt is null;
        plugin.IntegrityBlockedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _log.Error("Plugin {PluginId} NOT loaded: its files on disk ({Actual}) do not match the recorded hash ({Recorded})",
            plugin.PluginId, actual[..12], plugin.FilesSha256[..12]);

        if (firstTime && _notifications is not null)
            await _notifications.NotifyAdminsAsync(NotificationKinds.PluginIntegrity,
                $"{plugin.Name} was blocked",
                "Its files changed on disk without being installed or updated through Chronicle. Review them, then approve the change on the Plugins page if you made it.",
                "/plugins", dedupeKey: $"plugin.integrity:{plugin.PluginId}", ct: ct);
        return IntegrityOutcome.Blocked;
    }

    public async Task AcceptCurrentFilesAsync(Plugin plugin, CancellationToken ct = default)
    {
        var actual = ComputeFilesHash(plugin.DllPath);
        if (!string.Equals(plugin.FilesSha256, actual, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(plugin.FilesSha256)) plugin.PreviousFilesSha256 = plugin.FilesSha256;
            plugin.FilesSha256 = actual;
            plugin.FilesChangedAt = DateTime.UtcNow;
            _log.Information("Plugin {PluginId}: accepted new file hash {Hash}", plugin.PluginId, actual[..12]);
        }
        plugin.IntegrityBlockedAt = null;
        await _db.SaveChangesAsync(ct);
    }
}
