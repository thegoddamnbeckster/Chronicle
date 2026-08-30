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

        // Sync media types declared by loaded plugins into the media_types table.
        // This makes the type list fully plugin-driven: installing a new plugin that
        // declares a new type (e.g. "audiobooks") surfaces it everywhere without a migration.
        await SyncMediaTypesFromPluginsAsync(db, cancellationToken);

        // Seed media_enrichment rows for items enriched before the unified table was
        // introduced — restores enrichment status display for all pre-existing items.
        // Runs here (rather than in Program.cs's earlier migration block) specifically
        // because its per-plugin media-type filter needs the registry populated — every
        // plugin is now loaded and enabledPlugins/_registry both reflect that.
        var enrichmentService = scope.ServiceProvider.GetRequiredService<IMetadataEnrichmentService>();
        await enrichmentService.SeedEnrichmentRowsFromExternalIdsAsync(cancellationToken);

        // Backfill pre-existing Trakt-sourced credits (media_credits.person_media_item_id
        // didn't exist before this column was added) onto real people -- see
        // docs/plans/2026-08-28-people-section-design.md Section 1.2. Naturally idempotent:
        // the query is only ever non-empty for rows that haven't been resolved yet, so this
        // is a no-op on every startup after the first successful pass, and self-healing if an
        // earlier attempt only got partway through (e.g. app restart mid-backfill).
        await BackfillPersonCreditsAsync(db, scope.ServiceProvider, cancellationToken);

        // Resolves cast/crew already sitting in every item's cached metadata_json (from every
        // enrichment run before today) into real media_credits rows -- see
        // QueueCachedCreditsBackfillAsync's own doc for why this is fire-and-forget rather than
        // awaited here like the two backfills above: at library scale (tens of thousands of
        // items) it would otherwise add minutes to every single startup.
        QueueCachedCreditsBackfill();

        _log.Information("PluginHostService startup complete");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log.Information("PluginHostService stopping — all plugin load contexts will be unloaded");
        // PluginRegistry.Dispose() handles unloading via DI container disposal
        return Task.CompletedTask;
    }

    // ── Media type sync ───────────────────────────────────────────────────────

    /// <summary>
    /// Aggregates all <see cref="MediaTypeSupport"/> entries from every loaded metadata provider
    /// and file-scanner plugin, then upserts them into the <c>media_types</c> table so that
    /// the application's media type list is always derived from installed plugins rather than
    /// hard-coded DB migrations.
    ///
    /// Only entries with a non-empty <see cref="MediaTypeSupport.DisplayName"/> are synced;
    /// internal alias entries (e.g. the "movie" legacy alias for "movies") are skipped.
    /// Existing rows are updated in-place (preserving their PK / FK references); new rows are
    /// inserted with <c>IsBuiltIn = false</c>, <c>IsActive = true</c>.
    /// </summary>
    private async Task SyncMediaTypesFromPluginsAsync(ChronicleDbContext db, CancellationToken ct)
    {
        // Collect all MediaTypeSupport entries from every loaded plugin.
        var allSupport = _registry.GetMetadataProviders()
            .SelectMany(p => p.GetSupportedMediaTypes())
            .Concat(_registry.GetFileScannerPlugins()
                .SelectMany(p => p.GetSupportedMediaTypes()))
            .Where(s => !string.IsNullOrWhiteSpace(s.DisplayName))
            .GroupBy(s => s.MediaTypeName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // Merge declarations from multiple plugins for the same type.
                // Prefer richer values: non-default InteractionVerb/ProgressUnit, most levels, etc.
                var first = g.First();
                return new MediaTypeSupport
                {
                    MediaTypeName   = g.Key,
                    DisplayName     = g.Select(s => s.DisplayName).First(d => !string.IsNullOrWhiteSpace(d)),
                    HierarchyLevels = g.Max(s => s.HierarchyLevels),
                    HierarchyLabels = g.Select(s => s.HierarchyLabels).FirstOrDefault(h => h != null),
                    InteractionVerb = g.Select(s => s.InteractionVerb)
                                       .FirstOrDefault(v => v != "watched") ?? "watched",
                    ProgressUnit    = g.Select(s => s.ProgressUnit)
                                       .FirstOrDefault(u => u != "minutes") ?? "minutes",
                    // Any plugin declaring this type non-trackable wins -- a reference type stays
                    // a reference type even if another plugin's entry left IsTrackable at its
                    // (trackable) default.
                    IsTrackable     = g.All(s => s.IsTrackable),
                };
            })
            .ToList();

        if (allSupport.Count == 0)
            return;

        var existingTypes = await db.Set<Chronicle.Core.Models.MediaType>().ToListAsync(ct);

        var synced = 0;
        foreach (var support in allSupport)
        {
            var existing = existingTypes.FirstOrDefault(t =>
                string.Equals(t.Name, support.MediaTypeName, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                db.Set<Chronicle.Core.Models.MediaType>().Add(new Chronicle.Core.Models.MediaType
                {
                    Name            = support.MediaTypeName,
                    DisplayName     = support.DisplayName,
                    HierarchyLevels = support.HierarchyLevels,
                    HierarchyLabels = support.HierarchyLabels is { Length: > 0 }
                                        ? string.Join(",", support.HierarchyLabels)
                                        : null,
                    InteractionVerb = support.InteractionVerb,
                    ProgressUnit    = support.ProgressUnit,
                    IsBuiltIn       = false,
                    IsActive        = true,
                    IsTrackable     = support.IsTrackable,
                    CreatedAt       = DateTime.UtcNow,
                });
                _log.Information("MediaTypeSync: added new media type '{Name}' ({Display})",
                    support.MediaTypeName, support.DisplayName);
                synced++;
            }
            else
            {
                // Update mutable display/hierarchy fields, but never clobber DisplayName with blank.
                var changed = false;
                if (!string.IsNullOrWhiteSpace(support.DisplayName) && existing.DisplayName != support.DisplayName)
                { existing.DisplayName = support.DisplayName; changed = true; }
                if (support.HierarchyLevels > existing.HierarchyLevels)
                { existing.HierarchyLevels = support.HierarchyLevels; changed = true; }
                var wantLabels = support.HierarchyLabels is { Length: > 0 }
                    ? string.Join(",", support.HierarchyLabels) : null;
                if (wantLabels != null && existing.HierarchyLabels != wantLabels)
                { existing.HierarchyLabels = wantLabels; changed = true; }
                if (support.InteractionVerb != "watched" && existing.InteractionVerb != support.InteractionVerb)
                { existing.InteractionVerb = support.InteractionVerb; changed = true; }
                if (support.ProgressUnit != "minutes" && existing.ProgressUnit != support.ProgressUnit)
                { existing.ProgressUnit = support.ProgressUnit; changed = true; }
                if (existing.IsTrackable != support.IsTrackable)
                { existing.IsTrackable = support.IsTrackable; changed = true; }

                if (changed)
                {
                    _log.Information("MediaTypeSync: updated media type '{Name}' metadata", support.MediaTypeName);
                    synced++;
                }
            }
        }

        if (synced > 0)
            await db.SaveChangesAsync(ct);

        _log.Debug("MediaTypeSync: verified {Count} media type(s) from plugins ({Synced} changed)",
            allSupport.Count, synced);
    }

    // ── People backfill ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves every pre-existing Trakt-sourced media_credits row that has no
    /// PersonMediaItemId yet onto a real "people"-type MediaItem, using PersonName +
    /// ExternalPersonId (Trakt's own numeric person id, already captured on these rows) --
    /// best-effort, same resolution logic (PersonResolutionService.ResolvePersonOnlyAsync) new
    /// credits use going forward. Skipped entirely, quietly, if the "people" media type isn't
    /// registered yet (e.g. the Wikipedia plugin isn't installed) -- there's nothing to
    /// backfill onto.
    /// </summary>
    private async Task BackfillPersonCreditsAsync(ChronicleDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var peopleTypeExists = await db.MediaTypes.AnyAsync(t => t.Name == "people", ct);
        if (!peopleTypeExists)
            return;

        var unresolved = await db.MediaCredits
            .Where(c => c.Source == "trakt" && c.PersonMediaItemId == null)
            .ToListAsync(ct);
        if (unresolved.Count == 0)
            return;

        _log.Information("PeopleBackfill: resolving {Count} pre-existing Trakt credit row(s) onto people", unresolved.Count);
        var personResolutionService = services.GetRequiredService<IPersonResolutionService>();
        var resolved = 0;

        foreach (var credit in unresolved)
        {
            try
            {
                var person = await personResolutionService.ResolvePersonOnlyAsync(
                    db, credit.PersonName, credit.ExternalPersonId, credit.Source, ct);
                credit.PersonMediaItemId = person.Id;
                resolved++;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "PeopleBackfill: failed to resolve credit {CreditId} (\"{Name}\")",
                    credit.Id, credit.PersonName);
            }
        }

        await db.SaveChangesAsync(ct);
        _log.Information("PeopleBackfill: resolved {Resolved}/{Total} credit row(s)", resolved, unresolved.Count);
    }

    private const string CachedCreditsBackfillCompletedKey = "people.cached_credits_backfill_completed_at";

    /// <summary>
    /// Fire-and-forget wrapper around <see cref="BackfillCreditsFromCachedMetadataAsync"/> --
    /// deliberately NOT awaited by StartAsync, unlike the two backfills above. Those are
    /// naturally small (a handful of legacy rows); this one walks every media_item in the
    /// library, which for a mature Chronicle install (tens of thousands of items) would add
    /// real minutes to every single app startup if it blocked the host from serving requests.
    /// Uses its own DbContext scope since the one StartAsync used is disposed by the time this
    /// runs. Confirmed live (2026-08-30): a person credited on real library titles (Party Down,
    /// Step Brothers) showed only whatever credit happened to come from a title enriched AFTER
    /// the People feature shipped -- every other title's already-cached cast/crew data was
    /// sitting unused with zero media_credits rows to show for it, because credit resolution
    /// only ever ran on a fresh enrichment result, never retroactively.
    /// </summary>
    private void QueueCachedCreditsBackfill()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

                // Full-library scale means even the read-only "anything left to do" scan is
                // expensive -- once a full pass completes, skip entirely on every later startup
                // rather than re-scanning tens of thousands of items to confirm there's nothing
                // new (a fresh enrichment's own ResolveCreditsAsync keeps genuinely new credits
                // current going forward; this backfill only ever needed to run once).
                var alreadyDone = await db.AppSettings.AnyAsync(s => s.Key == CachedCreditsBackfillCompletedKey);
                if (alreadyDone)
                {
                    _log.Debug("CachedCreditsBackfill: already completed a prior pass — skipping");
                    return;
                }

                var personResolutionService = scope.ServiceProvider.GetRequiredService<IPersonResolutionService>();
                await BackfillCreditsFromCachedMetadataAsync(db, personResolutionService, CancellationToken.None);

                db.AppSettings.Add(new AppSetting
                {
                    Key   = CachedCreditsBackfillCompletedKey,
                    Value = DateTimeOffset.UtcNow.ToString("O"),
                });
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "CachedCreditsBackfill: background pass failed");
            }
        });
    }

    /// <summary>
    /// Walks every media_item's cached per-plugin metadata_json blob and resolves any cast/crew
    /// array found there into real media_credits rows via PersonResolutionService -- pure local
    /// reprocessing of data Chronicle already fetched, never a network call. Paged by id
    /// (keyset, not offset, so a later page doesn't re-scan earlier rows) with periodic
    /// SaveChanges + ChangeTracker.Clear() so the DbContext doesn't accumulate the whole
    /// library's worth of tracked entities in memory across the run. Skips any (item, source)
    /// pair that already has at least one media_credits row -- covers both a title genuinely
    /// re-enriched since the People feature shipped and a page this same pass already resolved.
    /// </summary>
    internal async Task BackfillCreditsFromCachedMetadataAsync(
        ChronicleDbContext db, IPersonResolutionService personResolutionService, CancellationToken ct)
    {
        var peopleTypeExists = await db.MediaTypes.AnyAsync(t => t.Name == "people", ct);
        if (!peopleTypeExists)
        {
            _log.Debug("CachedCreditsBackfill: skipped — \"people\" media type not registered yet");
            return;
        }

        var resolvedPairs = (await db.MediaCredits
            .Select(c => new { c.MediaItemId, c.Source })
            .Distinct()
            .ToListAsync(ct))
            .Select(x => (x.MediaItemId, x.Source))
            .ToHashSet();

        _log.Information("CachedCreditsBackfill: starting ({AlreadyResolved} (item, source) pair(s) already resolved)",
            resolvedPairs.Count);

        const int pageSize = 200;
        var lastId = 0;
        var itemsScanned = 0;
        var creditsResolved = 0;

        while (!ct.IsCancellationRequested)
        {
            var page = await db.MediaItems
                .Where(m => m.Id > lastId && m.MetadataJson != null)
                .OrderBy(m => m.Id)
                .Take(pageSize)
                .Select(m => new { m.Id, m.MetadataJson })
                .ToListAsync(ct);
            if (page.Count == 0) break;
            lastId = page[^1].Id;

            foreach (var item in page)
            {
                itemsScanned++;

                Dictionary<string, JsonElement>? blobs;
                try { blobs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.MetadataJson!); }
                catch (JsonException) { continue; }
                if (blobs is null) continue;

                foreach (var (pluginId, blob) in blobs)
                {
                    if (pluginId is "_resolved" or "_overrides") continue;
                    if (blob.ValueKind != JsonValueKind.Object) continue;

                    var source = Chronicle.Core.Helpers.PluginIdHelper.ToSource(pluginId);
                    if (!resolvedPairs.Add((item.Id, source))) continue;

                    var hasCast = blob.TryGetProperty("cast", out var castEl) && castEl.ValueKind == JsonValueKind.Array && castEl.GetArrayLength() > 0;
                    var hasCrew = blob.TryGetProperty("crew", out var crewEl) && crewEl.ValueKind == JsonValueKind.Array && crewEl.GetArrayLength() > 0;
                    if (!hasCast && !hasCrew) continue; // nothing to backfill for this pair

                    if (hasCast)
                    {
                        List<CastMember>? cast = null;
                        try { cast = JsonSerializer.Deserialize<List<CastMember>>(castEl.GetRawText()); }
                        catch (JsonException) { }

                        var billingOrder = 0;
                        foreach (var c in cast ?? [])
                        {
                            if (string.IsNullOrWhiteSpace(c.Name)) continue;
                            try
                            {
                                await personResolutionService.ResolveAndRecordCreditAsync(
                                    db, item.Id, c.Name, c.ExternalPersonId, source, c.ProfileImageUrl,
                                    role: "Actor", characterName: c.Role, billingOrder: billingOrder++, ct);
                                creditsResolved++;
                            }
                            catch (Exception ex)
                            {
                                _log.Warning(ex, "CachedCreditsBackfill: failed cast \"{Name}\" on item {ItemId}", c.Name, item.Id);
                            }
                        }
                    }

                    if (hasCrew)
                    {
                        List<CrewMember>? crew = null;
                        try { crew = JsonSerializer.Deserialize<List<CrewMember>>(crewEl.GetRawText()); }
                        catch (JsonException) { }

                        foreach (var c in crew ?? [])
                        {
                            if (string.IsNullOrWhiteSpace(c.Name)) continue;
                            try
                            {
                                await personResolutionService.ResolveAndRecordCreditAsync(
                                    db, item.Id, c.Name, c.ExternalPersonId, source, c.ProfileImageUrl,
                                    role: c.Job ?? "Crew", characterName: null, billingOrder: null, ct);
                                creditsResolved++;
                            }
                            catch (Exception ex)
                            {
                                _log.Warning(ex, "CachedCreditsBackfill: failed crew \"{Name}\" on item {ItemId}", c.Name, item.Id);
                            }
                        }
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            _log.Information(
                "CachedCreditsBackfill: progress — {Scanned} item(s) scanned, {Resolved} credit(s) resolved (through item id {LastId})",
                itemsScanned, creditsResolved, lastId);
        }

        _log.Information(
            "CachedCreditsBackfill: complete — {Scanned} item(s) scanned, {Resolved} credit(s) resolved",
            itemsScanned, creditsResolved);
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

                // If already registered, sync mutable fields from the manifest so that
                // renames (e.g. "Simkl" → "SIMKL") and task-description edits propagate
                // automatically on restart without a reinstall.
                var existingPlugin = await db.Plugins
                    .FirstOrDefaultAsync(p => p.PluginId == manifest.PluginId, ct);

                if (existingPlugin != null)
                {
                    // Update display fields — never touch IsEnabled, SettingsJson (user-controlled)
                    var pluginChanged = false;
                    var wantName  = manifest.Name ?? manifest.PluginId;
                    var wantVer   = manifest.Version ?? "0.0.0";
                    if (existingPlugin.Name            != wantName)            { existingPlugin.Name            = wantName;                  pluginChanged = true; }
                    if (existingPlugin.Version         != wantVer)             { existingPlugin.Version         = wantVer;                   pluginChanged = true; }
                    if (existingPlugin.IconUrl         != manifest.IconUrl)    { existingPlugin.IconUrl         = manifest.IconUrl;          pluginChanged = true; }
                    if (existingPlugin.BrandColorLight != manifest.BrandColorLight) { existingPlugin.BrandColorLight = manifest.BrandColorLight; pluginChanged = true; }
                    if (existingPlugin.BrandColorDark  != manifest.BrandColorDark)  { existingPlugin.BrandColorDark  = manifest.BrandColorDark;  pluginChanged = true; }
                    if (existingPlugin.FixMatchHint    != manifest.FixMatchHint)    { existingPlugin.FixMatchHint    = manifest.FixMatchHint;    pluginChanged = true; }
                    if (pluginChanged)
                    {
                        existingPlugin.UpdatedAt = DateTime.UtcNow;
                        registered = true;
                        _log.Information("Synced manifest metadata for plugin {PluginId}", manifest.PluginId);
                    }

                    // Sync task display names/descriptions and seed any tasks added to the manifest
                    // since initial install. CronExpression and IsEnabled are user-controlled and left alone.
                    if (manifest.BackgroundTasks is { Count: > 0 })
                    {
                        foreach (var tm in manifest.BackgroundTasks)
                        {
                            var namespacedId = $"{manifest.PluginId}:{tm.TaskId}";
                            var existingTask = await db.BackgroundTasks
                                .FirstOrDefaultAsync(t => t.TaskId == namespacedId, ct);

                            if (existingTask is null)
                            {
                                // New task added to manifest after initial install — seed it now
                                db.BackgroundTasks.Add(new BackgroundTask
                                {
                                    TaskId                 = namespacedId,
                                    PluginId               = manifest.PluginId,
                                    DisplayName            = tm.DisplayName ?? string.Empty,
                                    Description            = tm.Description ?? string.Empty,
                                    CronExpression         = tm.DefaultCron ?? string.Empty,
                                    IsEnabled              = tm.DefaultEnabled,
                                    Schedulable            = tm.Schedulable,
                                    RunConfirmationTitle   = tm.RunConfirmationTitle,
                                    RunConfirmationMessage = tm.RunConfirmationMessage,
                                });
                                registered = true;
                            }
                            else
                            {
                                var wantDisplay = tm.DisplayName ?? string.Empty;
                                var wantDesc    = tm.Description ?? string.Empty;
                                if (existingTask.DisplayName != wantDisplay || existingTask.Description != wantDesc)
                                {
                                    existingTask.DisplayName = wantDisplay;
                                    existingTask.Description = wantDesc;
                                    registered = true;
                                }
                            }
                        }
                    }
                    continue;
                }

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
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException ex)
        {
            _log.Error(ex, "Failed to deserialize plugin settings JSON — plugin will load with empty settings");
            return new Dictionary<string, string>();
        }
    }

}
