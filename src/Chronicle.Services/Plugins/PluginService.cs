using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Chronicle.Services.Plugins;

public class PluginService : IPluginService
{
    private readonly ChronicleDbContext _db;
    private readonly IPluginRegistry _registry;
    private readonly ILogger _log = Log.ForContext<PluginService>();

    public PluginService(ChronicleDbContext db, IPluginRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    public async Task<List<Plugin>> GetAllPluginsAsync() =>
        await _db.Plugins.OrderBy(p => p.Name).ToListAsync();

    public async Task<Plugin?> GetPluginAsync(int id) =>
        await _db.Plugins.FindAsync(id);

    public async Task<Plugin> InstallPluginAsync(string dllPath, CancellationToken ct = default)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"Plugin DLL not found: {dllPath}", dllPath);

        // Load the plugin first to read its manifest
        var tempSettings = new Dictionary<string, string>();
        var loaded = await _registry.LoadPluginAsync(0, dllPath, tempSettings, ct);
        var manifest = loaded.Manifest;

        // Check for duplicate
        var existing = await _db.Plugins.FirstOrDefaultAsync(p => p.PluginId == manifest.PluginId, ct);
        if (existing != null)
            throw new InvalidOperationException(
                $"Plugin '{manifest.PluginId}' is already installed (db id {existing.Id}).");

        var plugin = new Plugin
        {
            PluginId = manifest.PluginId,
            Name = manifest.Name,
            Version = manifest.Version,
            Author = manifest.Author,
            Description = manifest.Description,
            DllPath = dllPath,
            IsEnabled = true,
            InstalledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.Plugins.Add(plugin);
        await _db.SaveChangesAsync(ct);

        // Reload with the real db id
        _registry.UnloadPlugin(0);
        await _registry.LoadPluginAsync(plugin.Id, dllPath, tempSettings, ct);

        _log.Information("Installed plugin {PluginId} (db id {Id})", plugin.PluginId, plugin.Id);
        return plugin;
    }

    public async Task UpdateSettingsAsync(int id, Dictionary<string, string> settings)
    {
        var plugin = await _db.Plugins.FindAsync(id)
            ?? throw new InvalidOperationException($"Plugin with id {id} not found.");

        plugin.SettingsJson = JsonSerializer.Serialize(settings);
        plugin.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Reconfigure live providers (metadata + import) if the plugin is loaded
        if (plugin.IsEnabled)
        {
            var metaProvider = _registry.GetMetadataProvider(plugin.PluginId);
            if (metaProvider != null)
            {
                metaProvider.Configure(settings);
                _log.Information("Reconfigured live metadata provider {PluginId}", plugin.PluginId);
            }

            var importProvider = _registry.GetImportProvider(plugin.PluginId);
            if (importProvider != null)
            {
                importProvider.Configure(settings);
                _log.Information("Reconfigured live import provider {PluginId}", plugin.PluginId);
            }
        }
    }

    public async Task EnablePluginAsync(int id)
    {
        var plugin = await _db.Plugins.FindAsync(id)
            ?? throw new InvalidOperationException($"Plugin with id {id} not found.");

        plugin.IsEnabled = true;
        plugin.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var settings = DeserializeSettings(plugin.SettingsJson);
        await _registry.LoadPluginAsync(plugin.Id, plugin.DllPath, settings);

        _log.Information("Enabled plugin {PluginId}", plugin.PluginId);
    }

    public async Task DisablePluginAsync(int id)
    {
        var plugin = await _db.Plugins.FindAsync(id)
            ?? throw new InvalidOperationException($"Plugin with id {id} not found.");

        plugin.IsEnabled = false;
        plugin.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _registry.UnloadPlugin(id);
        _log.Information("Disabled plugin {PluginId}", plugin.PluginId);
    }

    public async Task UninstallPluginAsync(int id)
    {
        var plugin = await _db.Plugins.FindAsync(id)
            ?? throw new InvalidOperationException($"Plugin with id {id} not found.");

        _registry.UnloadPlugin(id);
        _db.Plugins.Remove(plugin);
        await _db.SaveChangesAsync();

        _log.Information("Uninstalled plugin {PluginId} (db id {Id})", plugin.PluginId, id);
    }

    public async Task<PluginHealthResult?> HealthCheckAsync(int id, CancellationToken ct = default)
    {
        var plugin = await _db.Plugins.FindAsync(new object[] { id }, ct);
        if (plugin is null) return null;

        var loaded = _registry.GetLoadedPlugins().FirstOrDefault(lp => lp.DbId == id);
        if (loaded is null) return null;

        // ── Detect missing required settings before hitting the network ─────────
        // A plugin that has never been configured returns false/throws without a
        // useful message; checking the schema first gives the user clear guidance.
        var missingLabels = GetMissingRequiredSettings(loaded, plugin.SettingsJson);
        if (missingLabels.Count > 0)
        {
            var noun = missingLabels.Count == 1 ? "setting" : "settings";
            return new PluginHealthResult(
                Healthy: false,
                FailureReason: $"Missing required {noun}: {string.Join(", ", missingLabels)}",
                IsCritical: false);
        }

        // ── Delegate to the plugin ───────────────────────────────────────────────
        // Try each provider type in turn — first one wins.
        try
        {
            bool result;
            if (loaded.MetadataProviders.Count > 0)
                result = await loaded.MetadataProviders[0].HealthCheckAsync(ct);
            else if (loaded.FileScannerPlugins.Count > 0)
                result = await loaded.FileScannerPlugins[0].HealthCheckAsync(ct);
            else if (loaded.ImportProviders.Count > 0)
                result = await loaded.ImportProviders[0].HealthCheckAsync(ct);
            else
                return null;

            return result
                ? new PluginHealthResult(Healthy: true)
                : new PluginHealthResult(
                    Healthy: false,
                    FailureReason: "Health check returned unhealthy.",
                    IsCritical: true);
        }
        catch (Exception ex)
        {
            // Classify: auth/config exceptions are non-critical (yellow).
            // Network, unexpected exceptions are critical (red).
            var isCritical = !IsConfigurationError(ex.Message);
            _log.Warning(ex, "Health check failed for plugin db-id {Id} (critical={Critical})", id, isCritical);
            return new PluginHealthResult(
                Healthy: false,
                FailureReason: ex.Message,
                IsCritical: isCritical);
        }
    }

    private static IReadOnlyDictionary<string, string> DeserializeSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// Returns the human-readable labels of required settings that are missing or blank.
    /// Only inspects the first metadata provider's schema (covers the primary use-case of
    /// e.g. TMDB needing an API key).
    /// </summary>
    private static List<string> GetMissingRequiredSettings(LoadedPlugin loaded, string? settingsJson)
    {
        if (loaded.MetadataProviders.Count == 0) return [];

        PluginSettingsSchema? schema;
        try { schema = loaded.MetadataProviders[0].GetSettingsSchema(); }
        catch { return []; }

        if (schema is null || schema.Settings.Count == 0) return [];

        Dictionary<string, string> current;
        try
        {
            current = string.IsNullOrWhiteSpace(settingsJson)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, string>>(settingsJson) ?? [];
        }
        catch { current = []; }

        return schema.Settings
            .Where(s => s.Required &&
                        (!current.ContainsKey(s.Key) || string.IsNullOrWhiteSpace(current[s.Key])))
            .Select(s => s.Label)
            .ToList();
    }

    /// <summary>
    /// Returns <c>true</c> when the exception message suggests a configuration or
    /// authentication problem (non-critical → yellow badge) rather than an unexpected
    /// runtime failure (critical → red badge).
    /// </summary>
    private static bool IsConfigurationError(string message)
    {
        var m = message.ToLowerInvariant();
        return m.Contains("api key")        ||
               m.Contains("apikey")         ||
               m.Contains("api_key")        ||
               m.Contains("token")          ||
               m.Contains("unauthorized")   ||
               m.Contains("unauthenticated")||
               m.Contains("not configured") ||
               m.Contains("configure")      ||
               m.Contains("credentials")    ||
               m.Contains("authentication") ||
               m.Contains(" 401")           ||
               m.Contains(" 403");
    }
}
