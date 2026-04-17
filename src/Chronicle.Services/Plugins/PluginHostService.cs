using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Chronicle.Services.Plugins;

/// <summary>
/// Background service that loads all enabled plugins from the database on application startup.
/// Also auto-registers any plugin DLL found in the plugins/ directory that is not yet in the DB,
/// so bundled plugins (TMDB, FileScanner) are available on a fresh install without a manual
/// catalog install step.
/// Plugins whose DLL is missing or fails to load are logged and skipped.
/// </summary>
public sealed class PluginHostService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPluginRegistry _registry;
    private readonly IPluginSettingsProtector _protector;
    private readonly string _contentRootPath;
    private readonly ILogger _log = Log.ForContext<PluginHostService>();

    // Framework DLL prefixes that are never the main plugin entry point
    private static readonly string[] _frameworkPrefixes =
    [
        "Microsoft.", "System.", "Newtonsoft.", "Serilog.", "TagLib",
        "Chronicle.Plugins.", "Chronicle.Core.", "Chronicle.Data.", "Chronicle.Services.",
    ];

    public PluginHostService(
        IServiceScopeFactory scopeFactory,
        IPluginRegistry registry,
        IPluginSettingsProtector protector,
        IHostEnvironment environment)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _protector = protector;
        _contentRootPath = environment.ContentRootPath;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _log.Information("PluginHostService starting — loading enabled plugins from database");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Auto-register any plugin folder that has a manifest.json but is not yet in the DB.
        // This makes bundled plugins (TMDB, FileScanner) available on a fresh install.
        await AutoRegisterBundledPluginsAsync(db, cancellationToken);

        var enabledPlugins = await db.Plugins
            .Where(p => p.IsEnabled)
            .ToListAsync(cancellationToken);

        _log.Information("Found {Count} enabled plugin(s) in database", enabledPlugins.Count);

        foreach (var plugin in enabledPlugins)
        {
            if (!File.Exists(plugin.DllPath))
            {
                _log.Warning(
                    "Plugin {PluginId} DLL not found at {DllPath} — skipping",
                    plugin.PluginId, plugin.DllPath);
                continue;
            }

            try
            {
                var settings = DeserializeSettings(plugin.SettingsJson);
                await _registry.LoadPluginAsync(plugin.Id, plugin.DllPath, settings, cancellationToken);
            }
            catch (Exception ex)
            {
                _log.Error(ex,
                    "Failed to load plugin {PluginId} from {DllPath}",
                    plugin.PluginId, plugin.DllPath);
            }
        }

        _log.Information("PluginHostService startup complete");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log.Information("PluginHostService stopping — all plugin load contexts will be unloaded");
        // PluginRegistry.Dispose() handles unloading via DI container disposal
        return Task.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the plugins/ directory for manifest.json sidecars and registers any plugin
    /// not already present in the database. This runs before the normal load loop so that
    /// newly discovered plugins are included in the enabled-plugins query.
    /// </summary>
    private async Task AutoRegisterBundledPluginsAsync(ChronicleDbContext db, CancellationToken ct)
    {
        var pluginsDir = Path.Combine(_contentRootPath, "plugins");
        if (!Directory.Exists(pluginsDir))
        {
            _log.Debug("No plugins/ directory found at {Path} — skipping auto-registration", pluginsDir);
            return;
        }

        var registered = false;

        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(
                    stream, cancellationToken: ct);

                if (string.IsNullOrWhiteSpace(manifest?.PluginId))
                    continue;

                // Skip if already registered (by any install path)
                if (await db.Plugins.AnyAsync(p => p.PluginId == manifest.PluginId, ct))
                    continue;

                var dllPath = FindPluginDll(dir, manifest.EntryType);
                if (dllPath is null)
                {
                    _log.Warning("manifest.json found in {Dir} but no plugin DLL located — skipping", dir);
                    continue;
                }

                db.Plugins.Add(new Plugin
                {
                    PluginId        = manifest.PluginId,
                    Name            = manifest.Name            ?? manifest.PluginId,
                    Version         = manifest.Version         ?? "0.0.0",
                    Author          = manifest.Author          ?? string.Empty,
                    Description     = manifest.Description,
                    DllPath         = dllPath,
                    IsEnabled       = true,
                    InstalledAt     = DateTime.UtcNow,
                    UpdatedAt       = DateTime.UtcNow,
                    IconUrl         = manifest.IconUrl,
                    BrandColorLight = manifest.BrandColorLight,
                    BrandColorDark  = manifest.BrandColorDark,
                    FixMatchHint    = manifest.FixMatchHint,
                });

                // Seed background tasks declared in the manifest
                if (manifest.BackgroundTasks is { Count: > 0 })
                    await PluginService.SeedPluginTasksAsync(db, manifest.PluginId, manifest.BackgroundTasks, ct);

                _log.Information("Auto-registered bundled plugin {PluginId} from {Dir}", manifest.PluginId, dir);
                registered = true;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to auto-register plugin from {Dir}", dir);
            }
        }

        if (registered)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns the primary plugin DLL from a plugin directory — the largest DLL
    /// that does not match any known framework prefix.
    /// </summary>
    private static string? FindPluginDll(string dir, string? entryType = null)
    {
        var candidates = Directory
            .GetFiles(dir, "*.dll")
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return !_frameworkPrefixes.Any(p =>
                    name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        // If entry_type is set, prefer the DLL whose name matches the namespace prefix.
        // e.g. "Chronicle.Plugin.FanEdit.FanEditMetadataProvider" → "Chronicle.Plugin.FanEdit.dll"
        if (!string.IsNullOrWhiteSpace(entryType))
        {
            // The assembly name is the longest prefix of entry_type that has a matching DLL.
            var parts = entryType.Split('.');
            for (var i = parts.Length - 1; i >= 1; i--)
            {
                var assemblyName = string.Join('.', parts[..i]) + ".dll";
                var match = candidates.FirstOrDefault(f =>
                    Path.GetFileName(f).Equals(assemblyName, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
        }

        // Fallback: largest remaining DLL
        return candidates
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    private IReadOnlyDictionary<string, string> DeserializeSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return new Dictionary<string, string>();

        var plainJson = _protector.Unprotect(settingsJson);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson)
               ?? new Dictionary<string, string>();
    }

}
