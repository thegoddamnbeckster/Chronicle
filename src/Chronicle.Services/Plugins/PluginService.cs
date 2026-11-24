using System.Text.Json;
using System.Text.Json.Nodes;
using Chronicle.Core.Helpers;
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
    private readonly IPluginRegistry   _registry;
    private readonly IPluginSettingsProtector _protector;
    private readonly IPluginIntegrity? _integrity;
    private readonly IMetadataResolutionService? _resolver;
    private readonly ILogger _log = Log.ForContext<PluginService>();

    // Per-plugin semaphore: serialises concurrent MergeSettingsAsync calls so that two
    // OAuth token refreshes for the same plugin can't overwrite each other's tokens.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
        _settingsLocks = new(StringComparer.OrdinalIgnoreCase);
    private static SemaphoreSlim GetSettingsLock(string pluginId) =>
        _settingsLocks.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));

    public PluginService(ChronicleDbContext db, IPluginRegistry registry, IPluginSettingsProtector protector,
        IPluginIntegrity? integrity = null, IMetadataResolutionService? resolver = null)
    {
        _db = db;
        _registry = registry;
        _protector = protector;
        _integrity = integrity;
        _resolver = resolver;
    }

    public async Task<List<Plugin>> GetAllPluginsAsync() =>
        await _db.Plugins.OrderBy(p => p.Name).ToListAsync();

    public async Task<Plugin?> GetPluginAsync(int id) =>
        await _db.Plugins.FindAsync(id);

    /// <summary>Refuses a plugin before any of its code runs: it must sit inside the plugins folder, carry a
    /// manifest.json naming an allowed plugin id, and (for a catalog install) be the plugin that was asked for.</summary>
    private async Task<string> GuardInstallAsync(string dllPath, string? catalogPluginId, CancellationToken ct)
    {
        if (_integrity is null) return catalogPluginId ?? string.Empty;   // unit tests that build the service without it

        if (!_integrity.IsInsidePluginsDirectory(dllPath))
            throw new PluginNotAllowedException(
                $"Plugins can only be installed from the plugins folder ({_integrity.PluginsDirectory}).");

        string? manifestId = null;
        var manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dllPath))!, "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                manifestId = (await JsonSerializer.DeserializeAsync<PluginManifest>(stream, cancellationToken: ct))?.PluginId;
            }
            catch (JsonException) { /* treated as missing below */ }
        }
        var pluginId = manifestId ?? catalogPluginId;
        if (string.IsNullOrWhiteSpace(pluginId))
            throw new PluginNotAllowedException("The plugin has no readable manifest.json naming its plugin id, so it cannot be checked.");
        if (catalogPluginId is not null && manifestId is not null
            && !string.Equals(catalogPluginId, manifestId, StringComparison.OrdinalIgnoreCase))
            throw new PluginNotAllowedException($"The downloaded files declare plugin {manifestId}, not the requested {catalogPluginId}.");
        if (!await _integrity.IsAllowedAsync(pluginId, ct))
            throw new PluginNotAllowedException(
                $"Plugin {pluginId} is not in the plugin catalog. Installing plugins from elsewhere is switched off " +
                $"(an administrator can allow it with the app setting {PluginIntegrity.AllowUnlistedKey}).");
        return pluginId;
    }

    public async Task AcceptCurrentFilesAsync(string pluginId, CancellationToken ct = default)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.PluginId == pluginId, ct)
            ?? throw new InvalidOperationException($"Plugin {pluginId} not found.");
        if (_integrity is null) return;

        // Files that declare a different plugin are never accepted as this one (a mislabelled release, or an
        // update/approval pointed at the wrong folder).
        var manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(plugin.DllPath))!, "manifest.json");
        if (File.Exists(manifestPath))
        {
            string? declared = null;
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                declared = (await JsonSerializer.DeserializeAsync<PluginManifest>(stream, cancellationToken: ct))?.PluginId;
            }
            catch (JsonException) { /* an unreadable manifest is refused below */ }
            if (!string.Equals(declared, pluginId, StringComparison.OrdinalIgnoreCase))
                throw new PluginNotAllowedException(
                    $"The files in the plugin folder declare plugin {declared ?? "(unreadable)"}, not {pluginId}, so they were not accepted.");
        }
        await _integrity.AcceptCurrentFilesAsync(plugin, ct);
    }

    /// <summary>Throws instead of loading a plugin whose files changed without Chronicle having been told.</summary>
    private async Task VerifyBeforeLoadAsync(Plugin plugin, CancellationToken ct = default)
    {
        if (_integrity is null) return;
        if (await _integrity.VerifyAsync(plugin, ct) == IntegrityOutcome.Blocked)
            throw new PluginNotAllowedException(
                $"Plugin {plugin.PluginId} was not loaded: its files on disk changed since they were installed. " +
                "If you made the change, approve it on the Plugins page.");
    }

    public async Task<Plugin> InstallPluginAsync(string dllPath, CancellationToken ct = default, string? catalogPluginId = null)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"Plugin DLL not found: {dllPath}", dllPath);

        var expectedId = await GuardInstallAsync(dllPath, catalogPluginId, ct);

        // Safety: discard any residual temp-id=0 registration left by a previous
        // failed install attempt before we load this plugin under that sentinel ID.
        _registry.UnloadPlugin(0);

        // Load the plugin first to read its manifest
        var tempSettings = new Dictionary<string, string>();
        var loaded = await _registry.LoadPluginAsync(0, dllPath, tempSettings, ct);
        var manifest = loaded.Manifest;

        if (_integrity is not null && !string.Equals(manifest.PluginId, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            _registry.UnloadPlugin(0);
            throw new PluginNotAllowedException(
                $"The plugin code declares id {manifest.PluginId} but its manifest.json says {expectedId}.");
        }

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
        if (_integrity is not null) await _integrity.AcceptCurrentFilesAsync(plugin, ct);

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

        // Reconfigure live providers (metadata + import) if the plugin is loaded. The settings
        // are already durably saved above at this point — a Configure() call throwing here (e.g.
        // a provider validating a required field, or a bad value it can't use to build a client)
        // must not turn into a 500 for what the user experiences as a successful save. Log and
        // move on; the next enable/reload picks up the saved settings correctly regardless.
        if (plugin.IsEnabled)
        {
            var metaProvider = _registry.GetMetadataProvider(plugin.PluginId);
            if (metaProvider != null)
            {
                try
                {
                    metaProvider.Configure(settings);
                    _log.Information("Reconfigured live metadata provider {PluginId}", plugin.PluginId);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex,
                        "Settings saved for {PluginId}, but reconfiguring the live metadata provider failed",
                        plugin.PluginId);
                }
            }

            var importProvider = _registry.GetImportProvider(plugin.PluginId);
            if (importProvider != null)
            {
                try
                {
                    importProvider.Configure(settings);
                    _log.Information("Reconfigured live import provider {PluginId}", plugin.PluginId);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex,
                        "Settings saved for {PluginId}, but reconfiguring the live import provider failed",
                        plugin.PluginId);
                }
            }

            foreach (var locator in _registry.GetMediaLocators().Where(l => l.PluginId == plugin.PluginId))
            {
                try
                {
                    locator.Configure(settings);
                    _log.Information("Reconfigured live media locator {PluginId}", plugin.PluginId);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex,
                        "Settings saved for {PluginId}, but reconfiguring the live media locator failed",
                        plugin.PluginId);
                }
            }
        }
    }

    public async Task MergeSettingsAsync(
        string pluginId,
        IReadOnlyDictionary<string, string> newSettings,
        CancellationToken ct = default)
    {
        var sem = GetSettingsLock(pluginId);
        await sem.WaitAsync(ct);
        try
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
        finally
        {
            sem.Release();
        }
    }

    public async Task EnablePluginAsync(int id)
    {
        var plugin = await _db.Plugins.FindAsync(id)
            ?? throw new InvalidOperationException($"Plugin with id {id} not found.");

        plugin.IsEnabled = true;
        plugin.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await VerifyBeforeLoadAsync(plugin);
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

    // ── Removing a plugin ─────────────────────────────────────────────────────
    //
    // One process, always: a plugin is removed together with everything it put in Chronicle -- never merely hidden. Removal is
    // recorded first (a marker in app_settings, the plugin disabled and unloaded), then done in small atomic steps, and the
    // plugin's own record is deleted LAST. A removal cut short (a crash, a restart, a locked database) therefore leaves the
    // plugin visibly "being removed" and is finished on the next start (PluginHostService); it is never half-removed in a way
    // nobody can tell. One enormous transaction would hold SQLite's write lock for minutes, blocking every other task, for a
    // plugin that touched tens of thousands of items.

    /// <summary>The app_settings key that marks a plugin's removal as begun and not yet finished.</summary>
    public const string RemovalMarkerPrefix = "plugin_removal.";
    public static string RemovalMarkerKey(string pluginId) => RemovalMarkerPrefix + pluginId;

    private const int PurgeChunk = 2000;
    private const int PartitionBatch = 200;

    public async Task UninstallPluginAsync(int id)
    {
        var plugin = await _db.Plugins.FindAsync(id)
            ?? throw new InvalidOperationException($"Plugin with id {id} not found.");

        plugin.IsEnabled = false;
        plugin.UpdatedAt = DateTime.UtcNow;
        var key = RemovalMarkerKey(plugin.PluginId);
        var marker = await _db.AppSettings.FindAsync(key);
        if (marker is null) _db.AppSettings.Add(new AppSetting { Key = key, Value = DateTimeOffset.UtcNow.ToString("O") });
        await _db.SaveChangesAsync();

        await CompleteRemovalAsync(plugin.PluginId);
    }

    public async Task CompleteRemovalAsync(string pluginId, CancellationToken ct = default)
    {
        var plugin = await _db.Plugins.AsNoTracking().FirstOrDefaultAsync(p => p.PluginId == pluginId, ct);
        if (plugin is not null) _registry.UnloadPlugin(plugin.Id);   // a no-op when it is not loaded
        var folder = plugin is null ? null : Path.GetDirectoryName(Path.GetFullPath(plugin.DllPath));
        var sourceKey = PluginIdHelper.ToSource(pluginId);

        // Every step removes only what is still there, so a run cut short is simply run again.
        var enrichment = await RemoveInChunksAsync(() => _db.MediaEnrichments.Where(e => e.PluginId == pluginId), ct);

        // MediaExternalIds, MediaCredits, PersonHeadshots and PersonProviderCredits are all keyed by the SHORT source form
        // (PluginIdHelper.ToSource), not the full plugin id: confirmed against every write site.
        var externalIds = await RemoveInChunksAsync(() => _db.MediaExternalIds.Where(e => e.Source == sourceKey), ct);
        var credits     = await RemoveInChunksAsync(() => _db.MediaCredits.Where(c => c.Source == sourceKey), ct);
        var headshots   = await RemoveInChunksAsync(() => _db.PersonHeadshots.Where(h => h.Source == sourceKey), ct);
        var personCredits = await RemoveInChunksAsync(() => _db.PersonProviderCredits.Where(c => c.Source == sourceKey), ct);
        var tasks       = await RemoveInChunksAsync(() => _db.BackgroundTasks.Where(t => t.PluginId == pluginId), ct);
        var notices     = await RemoveInChunksAsync(() => _db.Notifications.Where(n => n.DedupeKey != null && n.DedupeKey.Contains(pluginId)), ct);

        var items = await StripPluginDataFromItemsAsync(pluginId, ct);
        var settings = await ScrubPluginFromSettingsAsync(pluginId, ct);

        // The plugin's own record and the marker go together, last: until this commits the plugin is still listed, as being removed.
        _db.ChangeTracker.Clear();
        var row = await _db.Plugins.FirstOrDefaultAsync(p => p.PluginId == pluginId, ct);
        if (row is not null) _db.Plugins.Remove(row);
        var marker = await _db.AppSettings.FindAsync([RemovalMarkerKey(pluginId)], ct);
        if (marker is not null) _db.AppSettings.Remove(marker);
        await _db.SaveChangesAsync(ct);

        // Files last, and never inside the database work: they cannot be rolled back. Only the plugin's own folder inside the
        // plugins folder is removed (its DLL, its manifest, and whatever data it kept there).
        var files = RemovePluginFolder(folder);

        _log.Information(
            "Removed plugin {PluginId} -- {EnrichmentCount} enrichment rows, {ExternalIdCount} external ids, {CreditCount} credits, " +
            "{HeadshotCount} headshots, {PersonCreditCount} person credits, {TaskCount} background tasks, {NoticeCount} notifications, " +
            "data on {ItemCount} items re-resolved, {SettingCount} settings cleaned, folder {Files}",
            pluginId, enrichment, externalIds, credits, headshots, personCredits, tasks, notices, items, settings, files);
    }

    /// <summary>Deletes every row the query returns, a chunk at a time (each chunk atomic), and returns how many there were.</summary>
    private async Task<int> RemoveInChunksAsync<T>(Func<IQueryable<T>> query, CancellationToken ct) where T : class
    {
        var total = 0;
        while (true)
        {
            var chunk = await query().Take(PurgeChunk).ToListAsync(ct);
            if (chunk.Count == 0) return total;
            _db.RemoveRange(chunk);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            total += chunk.Count;
        }
    }

    /// <summary>
    /// Removes the plugin's own top-level key from MetadataJson on every item that has one and re-resolves each such item, so
    /// nothing the plugin supplied lingers in the resolved values either. Deliberately NOT scoped to items with an enrichment row:
    /// a movie-collection container's partition is written directly with none. In id order, a small batch at a time (each
    /// atomic); an item already stripped no longer matches, so a run cut short continues where it stopped.
    /// </summary>
    private async Task<int> StripPluginDataFromItemsAsync(string pluginId, CancellationToken ct)
    {
        var stripped = 0;
        var lastId = 0;
        while (true)
        {
            var batch = await _db.MediaItems.Include(m => m.MediaType)
                .Where(m => m.Id > lastId && m.MetadataJson != null && m.MetadataJson.Contains(pluginId))
                .OrderBy(m => m.Id).Take(PartitionBatch).ToListAsync(ct);
            if (batch.Count == 0) return stripped;
            lastId = batch[^1].Id;

            foreach (var item in batch)
            {
                try
                {
                    var root = JsonNode.Parse(item.MetadataJson!)?.AsObject();
                    if (root is null || !root.Remove(pluginId)) continue;   // a substring match only: nothing to remove
                    item.MetadataJson = root.ToJsonString();
                    if (_resolver is not null) await _resolver.ResolveAsync(item, _db, ct);
                    stripped++;
                }
                catch (JsonException)
                {
                    // Malformed JSON on this row predates this cleanup -- leave it alone rather than risk losing whatever else is in it.
                }
            }
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>Takes the plugin out of the settings that name plugins: the per-field priority lists (Metadata Assignment), the
    /// display order, the task order, and any setting keyed by the plugin's id. Returns how many settings changed.</summary>
    private async Task<int> ScrubPluginFromSettingsAsync(string pluginId, CancellationToken ct)
    {
        var changed = 0;
        var settings = await _db.AppSettings
            .Where(s => s.Key == "metadata_assignment.config" || s.Key == "plugin_display_order.config" || s.Key == "scheduler.task_priority_order"
                     || (s.Key.Contains(pluginId) && s.Key != RemovalMarkerKey(pluginId) && s.Key != "plugins.catalog_cache"))
            .ToListAsync(ct);
        foreach (var s in settings)
        {
            if (s.Key.Contains(pluginId)) { _db.AppSettings.Remove(s); changed++; continue; }
            try
            {
                var node = JsonNode.Parse(s.Value);
                if (node is not null && RemovePluginReferences(node, pluginId)) { s.Value = node.ToJsonString(); changed++; }
            }
            catch (JsonException) { /* not JSON: leave it */ }
        }
        if (changed > 0) await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return changed;
    }

    /// <summary>Removes the plugin from every array of ids ("pluginId" or "pluginId:task") and every property named for it. True when anything changed.</summary>
    internal static bool RemovePluginReferences(JsonNode node, string pluginId)
    {
        var changed = false;
        switch (node)
        {
            case JsonArray array:
                for (var i = array.Count - 1; i >= 0; i--)
                {
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var s)
                        && (string.Equals(s, pluginId, StringComparison.OrdinalIgnoreCase) || s.StartsWith(pluginId + ":", StringComparison.OrdinalIgnoreCase)))
                    { array.RemoveAt(i); changed = true; }
                    else if (array[i] is { } child) changed |= RemovePluginReferences(child, pluginId);
                }
                break;
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (string.Equals(name, pluginId, StringComparison.OrdinalIgnoreCase)) { obj.Remove(name); changed = true; }
                    else if (obj[name] is { } child) changed |= RemovePluginReferences(child, pluginId);
                }
                break;
        }
        return changed;
    }

    /// <summary>Deletes the plugin's own folder, only when it is inside the plugins folder and is not the plugins folder itself.
    /// Returns what happened, in words, for the log.</summary>
    private string RemovePluginFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return "already gone";
        if (_integrity is null) return "left (no plugins folder known)";
        var trim = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        if (!_integrity.IsInsidePluginsDirectory(folder)
            || string.Equals(Path.GetFullPath(folder).TrimEnd(trim), Path.GetFullPath(_integrity.PluginsDirectory).TrimEnd(trim), StringComparison.OrdinalIgnoreCase))
            return "left (not inside the plugins folder)";
        try { Directory.Delete(folder, recursive: true); return "deleted"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Could not delete the folder of a removed plugin: {Folder}", folder);
            return "left (could not be deleted)";
        }
    }

    public Task<bool> UnloadFromRegistryAsync(string pluginId)
    {
        var plugin = _registry.GetLoadedPlugins()
            .FirstOrDefault(p => string.Equals(p.Manifest.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));

        if (plugin is not null)
        {
            _registry.UnloadPlugin(plugin.DbId);
            _log.Information("Unloaded plugin {PluginId} from registry", pluginId);
            return Task.FromResult(true);
        }

        _log.Warning("UnloadFromRegistry: plugin {PluginId} was not loaded", pluginId);
        return Task.FromResult(false);
    }

    public async Task ReloadPluginAsync(string pluginId, CancellationToken ct = default)
    {
        var plugin = await _db.Plugins
            .FirstOrDefaultAsync(p => p.PluginId == pluginId, ct)
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' not found.");

        if (!plugin.IsEnabled)
            throw new InvalidOperationException($"Plugin '{pluginId}' is disabled — enable it before reloading.");

        if (!File.Exists(plugin.DllPath))
            throw new FileNotFoundException($"Plugin DLL not found at '{plugin.DllPath}'.", plugin.DllPath);

        await VerifyBeforeLoadAsync(plugin, ct);
        var settings = DeserializeSettings(plugin.SettingsJson);
        await _registry.LoadPluginAsync(plugin.Id, plugin.DllPath, settings, ct);

        // Seed any enrichment rows that may be missing — harmless for existing rows.
        // This matches the behaviour of EnablePluginAsync and ensures the plugin
        // appears in Enrichment Status after a hot-reload.
        var reloadedProvider = _registry.GetMetadataProvider(plugin.PluginId);
        if (reloadedProvider is not null)
            await SeedEnrichmentRowsForProviderAsync(plugin.PluginId, reloadedProvider, ct);

        _log.Information("Reloaded plugin {PluginId} from {DllPath}", pluginId, plugin.DllPath);
    }

    public async Task<Plugin> MarkUpdatedAsync(string pluginId, CancellationToken ct = default)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.PluginId == pluginId, ct)
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' not found.");

        var loaded = _registry.GetLoadedPlugins().FirstOrDefault(lp => lp.DbId == plugin.Id);
        if (loaded is not null)
            plugin.Version = loaded.Manifest.Version;

        plugin.LatestVersionAvailable = null;
        plugin.UpdateCheckedAt        = DateTime.UtcNow;
        plugin.UpdatedAt              = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return plugin;
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
                result = await ProviderCallGuard.CallAsync(
                    t => loaded.MetadataProviders[0].HealthCheckAsync(t), plugin.PluginId, "HealthCheckAsync", false,
                    msg => _log.Warning(msg), msg => _log.Error(msg), ct);
            else if (loaded.FileScannerPlugins.Count > 0)
                result = await ProviderCallGuard.CallAsync(
                    t => loaded.FileScannerPlugins[0].HealthCheckAsync(t), plugin.PluginId, "HealthCheckAsync", false,
                    msg => _log.Warning(msg), msg => _log.Error(msg), ct);
            else if (loaded.ImportProviders.Count > 0)
                result = await ProviderCallGuard.CallAsync(
                    t => loaded.ImportProviders[0].HealthCheckAsync(t), plugin.PluginId, "HealthCheckAsync", false,
                    msg => _log.Warning(msg), msg => _log.Error(msg), ct);
            else if (loaded.MediaLocators.Count > 0)
                result = await ProviderCallGuard.CallAsync(
                    t => loaded.MediaLocators[0].HealthCheckAsync(t), plugin.PluginId, "HealthCheckAsync", false,
                    msg => _log.Warning(msg), msg => _log.Error(msg), ct);
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
    /// <see cref="MediaItem"/> whose media type is supported by <paramref name="provider"/>
    /// and whose hierarchy level is within the range the provider can service.
    /// Rows that already exist are skipped.
    /// </summary>
    private async Task SeedEnrichmentRowsForProviderAsync(
        string manifestPluginId, IMetadataProvider provider, CancellationToken ct = default)
    {
        var supportedTypes = provider.GetSupportedMediaTypes();

        // Build a map of DB media_type id → max hierarchy level the provider supports.
        // HierarchyLevels = N means levels 0..N-1 are supported. A plugin that declares
        // HierarchyLevels = 2 for music (Artist + Album) must not seed rows for level-2
        // tracks even though those items share the same media_type_id.
        var typeNameToMaxLevel = supportedTypes.ToDictionary(
            t => t.MediaTypeName,
            t => t.HierarchyLevels - 1,  // max supported level (0-based)
            StringComparer.OrdinalIgnoreCase);

        var typeNameList = typeNameToMaxLevel.Keys.ToList();
        var dbTypes = await _db.MediaTypes
            .Where(mt => typeNameList.Contains(mt.Name))
            .Select(mt => new { mt.Id, mt.Name })
            .ToListAsync(ct);

        if (dbTypes.Count == 0)
            return;

        // Map DB type id → max supported level for the WHERE clause.
        var typeIdToMaxLevel = dbTypes.ToDictionary(
            t => t.Id,
            t => typeNameToMaxLevel[t.Name]);

        var supportedTypeIds = dbTypes.Select(t => t.Id).ToList();

        // Fetch all items of the supported types, then filter in-memory by max level.
        // Dictionary lookup can't be translated to SQL, so we materialise and filter here.
        var allItems = await _db.MediaItems
            .Where(i => supportedTypeIds.Contains(i.MediaTypeId))
            .Select(i => new { i.Id, i.MediaTypeId, i.HierarchyLevel })
            .ToListAsync(ct);

        var itemIds = allItems
            .Where(i => i.HierarchyLevel <= typeIdToMaxLevel[i.MediaTypeId])
            .Select(i => i.Id)
            .ToList();

        if (itemIds.Count == 0)
            return;

        // Query all enrichment rows for this plugin without an IN clause (avoids
        // SQLite SQLITE_LIMIT_VARIABLE_NUMBER=999 for large libraries), then filter
        // in-memory against the already-loaded itemIds set.
        var itemIdSet = itemIds.ToHashSet();
        var existingSet = (await _db.MediaEnrichments
            .Where(x => x.PluginId == manifestPluginId)
            .Select(x => x.MediaItemId)
            .ToListAsync(ct))
            .Where(id => itemIdSet.Contains(id))
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
        if (loaded.MetadataProviders.Count == 0 && loaded.MediaLocators.Count == 0) return [];

        PluginSettingsSchema? schema;
        try
        {
            schema = loaded.MetadataProviders.Count > 0
                ? loaded.MetadataProviders[0].GetSettingsSchema()
                : loaded.MediaLocators[0].GetSettingsSchema();
        }
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
                    // Without a next run time a scheduled task never starts (the scheduler only fires tasks that are due).
                    NextRunAt              = TaskSchedulerService.GetNextOccurrence(task.DefaultCron ?? string.Empty),
                    Schedulable            = task.Schedulable,
                    RunConfirmationTitle   = task.RunConfirmationTitle,
                    RunConfirmationMessage = task.RunConfirmationMessage,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
