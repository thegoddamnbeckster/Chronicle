using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;
using BackgroundTask = Chronicle.Core.Models.BackgroundTask;

namespace Chronicle.Services.Plugins;

public class PluginService : IPluginService
{
    private readonly ChronicleDbContext _db;
    private readonly IPluginRegistry _registry;
    private readonly IPluginSettingsProtector _protector;
    private readonly ILogger _log = Log.ForContext<PluginService>();

    public PluginService(ChronicleDbContext db, IPluginRegistry registry, IPluginSettingsProtector protector)
    {
        _db = db;
        _registry = registry;
        _protector = protector;
    }

    public async Task<List<Plugin>> GetAllPluginsAsync() =>
        await _db.Plugins.OrderBy(p => p.Name).ToListAsync();

    public async Task<Plugin?> GetPluginAsync(int id) =>
        await _db.Plugins.FindAsync(id);

    public async Task<Plugin> InstallPluginAsync(string dllPath, CancellationToken ct = default)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"Plugin DLL not found: {dllPath}", dllPath);

        // Safety: discard any residual temp-id=0 registration left by a previous
        // failed install attempt before we load this plugin under that sentinel ID.
        _registry.UnloadPlugin(0);

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
            PluginId        = manifest.PluginId,
            Name            = manifest.Name,
            Version         = manifest.Version,
            Author          = manifest.Author,
            Description     = manifest.Description,
            DllPath         = dllPath,
            IsEnabled       = true,
            InstalledAt     = DateTime.UtcNow,
            UpdatedAt       = DateTime.UtcNow,
            IconUrl         = manifest.IconUrl,
            BrandColorLight = manifest.BrandColorLight,
            BrandColorDark  = manifest.BrandColorDark,
            FixMatchHint    = manifest.FixMatchHint,
        };

        _db.Plugins.Add(plugin);
        await _db.SaveChangesAsync(ct);

        // Seed background tasks declared in the manifest
        if (manifest.BackgroundTasks is { Count: > 0 })
            await SeedPluginTasksAsync(_db, manifest.PluginId, manifest.BackgroundTasks, ct);

        // Reload with the real db id
        _registry.UnloadPlugin(0);
        await _registry.LoadPluginAsync(plugin.Id, dllPath, tempSettings, ct);

        // If this is a metadata provider, seed pending enrichment rows for all existing items
        var installedProvider = _registry.GetMetadataProvider(plugin.PluginId);
        if (installedProvider is not null)
            await SeedEnrichmentRowsForProviderAsync(plugin.PluginId, installedProvider, ct);

        _log.Information("Installed plugin {PluginId} (db id {Id})", plugin.PluginId, plugin.Id);
        return plugin;
    }

    public async Task UpdateSettingsAsync(int id, Dictionary<string, string> settings)
    {
        var plugin = await _db.Plugins.FindAsync(id)
            ?? throw new InvalidOperationException($"Plugin with id {id} not found.");

        plugin.SettingsJson = _protector.Protect(JsonSerializer.Serialize(settings));
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

    public async Task MergeSettingsAsync(
        string pluginId,
        IReadOnlyDictionary<string, string> newSettings,
        CancellationToken ct = default)
    {
        var plugin = await _db.Plugins
            .FirstOrDefaultAsync(p => p.PluginId == pluginId, ct)
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' not found.");

        var existing = DeserializeSettings(plugin.SettingsJson);
        var merged   = new Dictionary<string, string>(existing);
        foreach (var (key, value) in newSettings)
            merged[key] = value;

        await UpdateSettingsAsync(plugin.Id, merged);
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

        // If this is a metadata provider, seed pending enrichment rows for all existing items
        var enabledProvider = _registry.GetMetadataProvider(plugin.PluginId);
        if (enabledProvider is not null)
            await SeedEnrichmentRowsForProviderAsync(plugin.PluginId, enabledProvider);

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

        // Remove background tasks seeded by this plugin.  Enrichment rows in
        // media_enrichment are intentionally kept — they record historical data and
        // safe INSERT-IF-MISSING seeding on reinstall will skip them.
        var tasks = await _db.BackgroundTasks
            .Where(t => t.PluginId == plugin.PluginId)
            .ToListAsync();
        if (tasks.Count > 0)
            _db.BackgroundTasks.RemoveRange(tasks);

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

    /// <summary>
    /// Inserts pending <see cref="MediaItemEnrichment"/> rows for every existing
    /// <see cref="MediaItem"/> whose media type is supported by <paramref name="provider"/>.
    /// Rows that already exist are skipped.
    /// </summary>
    private async Task SeedEnrichmentRowsForProviderAsync(
        string manifestPluginId, IMetadataProvider provider, CancellationToken ct = default)
    {
        var supportedTypeNames = provider.GetSupportedMediaTypes()
            .Select(t => t.MediaTypeName)
            .ToList();

        var supportedTypeIds = await _db.MediaTypes
            .Where(mt => supportedTypeNames.Contains(mt.Name))
            .Select(mt => mt.Id)
            .ToListAsync(ct);

        if (supportedTypeIds.Count == 0)
            return;

        var itemIds = await _db.MediaItems
            .Where(i => supportedTypeIds.Contains(i.MediaTypeId))
            .Select(i => i.Id)
            .ToListAsync(ct);

        if (itemIds.Count == 0)
            return;

        var existingSet = (await _db.MediaEnrichments
            .Where(x => x.PluginId == manifestPluginId && itemIds.Contains(x.MediaItemId))
            .Select(x => x.MediaItemId)
            .ToListAsync(ct))
            .ToHashSet();

        foreach (var itemId in itemIds)
        {
            if (existingSet.Contains(itemId))
                continue;

            _db.MediaEnrichments.Add(new MediaItemEnrichment
            {
                MediaItemId = itemId,
                PluginId    = manifestPluginId,
                Status      = EnrichmentStatus.Pending,
                MaxRetries  = 3,
            });
        }

        await _db.SaveChangesAsync(ct);

        _log.Information(
            "Seeded {Count} pending enrichment rows for provider {PluginId}",
            itemIds.Count - existingSet.Count, manifestPluginId);
    }

    private IReadOnlyDictionary<string, string> DeserializeSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();
        var plainJson = _protector.Unprotect(json);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson)
            ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// Returns the human-readable labels of required settings that are missing or blank.
    /// Only inspects the first metadata provider's schema (covers the primary use-case of
    /// e.g. TMDB needing an API key).
    /// </summary>
    private List<string> GetMissingRequiredSettings(LoadedPlugin loaded, string? settingsJson)
    {
        if (loaded.MetadataProviders.Count == 0) return [];

        PluginSettingsSchema? schema;
        try { schema = loaded.MetadataProviders[0].GetSettingsSchema(); }
        catch { return []; }

        if (schema is null || schema.Settings.Count == 0) return [];

        Dictionary<string, string> current;
        try
        {
            var plainJson = _protector.Unprotect(settingsJson);
            current = JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson) ?? [];
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

    /// <summary>
    /// Inserts one <see cref="BackgroundTask"/> row per task declared in the plugin manifest.
    /// Uses INSERT-IF-MISSING semantics: existing rows (possibly customised by the user) are
    /// never overwritten. The namespaced task ID stored in the DB is
    /// <c>{pluginId}:{taskId}</c> (e.g. <c>chronicle.plugin.tmdb:fetch-missing-metadata</c>).
    /// </summary>
    internal static async Task SeedPluginTasksAsync(
        ChronicleDbContext db,
        string pluginId,
        IReadOnlyList<PluginTaskManifest> tasks,
        CancellationToken ct = default)
    {
        foreach (var task in tasks)
        {
            var namespacedId = $"{pluginId}:{task.TaskId}";
            var exists = await db.BackgroundTasks
                .AnyAsync(t => t.TaskId == namespacedId, ct);

            if (!exists)
            {
                db.BackgroundTasks.Add(new BackgroundTask
                {
                    TaskId                 = namespacedId,
                    PluginId               = pluginId,
                    DisplayName            = task.DisplayName,
                    Description            = task.Description ?? string.Empty,
                    CronExpression         = task.DefaultCron ?? string.Empty,
                    IsEnabled              = task.DefaultEnabled,
                    Schedulable            = task.Schedulable,
                    RunConfirmationTitle   = task.RunConfirmationTitle,
                    RunConfirmationMessage = task.RunConfirmationMessage,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
