using System.Diagnostics;
using System.Text.Json;
using Chronicle.Core.Exceptions;
using PluginAuthException = Chronicle.Plugins.PluginAuthException;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class MetadataEnrichmentService(
    IServiceScopeFactory scopeFactory,
    IMetadataResolutionService resolutionService,
    IMovieCollectionService movieCollectionService,
    IMetadataUrlValidator urlValidator,
    ILogger<MetadataEnrichmentService> logger) : IMetadataEnrichmentService
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromHours(24);
    private static readonly NfoSignalExtractor _nfoExtractor = new();

    // Camelcase options used when serialising MediaMetadata objects into metadata_json plugin
    // blobs.  MetadataResolutionService.FieldMap and MediaController._resolved reads all use
    // camelCase keys ("posterUrl", "title", …); using default options would produce PascalCase
    // ("PosterUrl") which TryGetProperty cannot find (case-sensitive).
    internal static readonly JsonSerializerOptions MetadataBlobOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Per-plugin semaphore: prevents two concurrent batch runs for the same plugin
    // (e.g. scheduled task + manual Run Now firing simultaneously).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
        _pluginLocks = new(StringComparer.OrdinalIgnoreCase);

    // ── Unified entry points ───────────────────────────────────────────────────

    public async Task EnrichItemAsync(
        int mediaItemId, string pluginId,
        EnrichmentOptions options, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var item = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.Parent)
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return;

        var provider = registry.GetMetadataProvider(pluginId);
        if (provider is null) return;

        var allProviders = registry.GetMetadataProviderEntries()
            .Select(e => (e.PluginId, (IMetadataProvider)e.Provider))
            .ToList();
        // Cross-ref seeding and SaveChangesAsync already happen inside EnrichItemCoreAsync.
        await EnrichItemCoreAsync(db, provider, pluginId, item, options, ct, allProviders);
    }

    public async Task EnrichItemAsync(
        int mediaItemId, EnrichmentOptions options, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var item = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.Parent)
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return;

        var allProviders = registry.GetMetadataProviderEntries()
            .Select(e => (e.PluginId, (IMetadataProvider)e.Provider))
            .ToList();
        var mediaTypeName = NormalizeMediaTypeName(item.MediaType?.Name ?? string.Empty);

        foreach (var (pluginId, provider, _) in registry.GetMetadataProviderEntries())
        {
            ct.ThrowIfCancellationRequested();
            var supported = provider.GetSupportedMediaTypes()
                .Any(t => string.Equals(
                    NormalizeMediaTypeName(t.MediaTypeName), mediaTypeName,
                    StringComparison.OrdinalIgnoreCase));
            if (!supported) continue;

            try
            {
                // Cross-ref seeding and SaveChangesAsync already happen inside EnrichItemCoreAsync.
                await EnrichItemCoreAsync(db, provider, pluginId, item, options, ct, allProviders);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "EnrichItemAsync all-plugins: plugin {P} failed for item {Id}", pluginId, mediaItemId);
            }
        }
    }

    public async Task<IReadOnlyList<EnrichmentRecord>> GetEnrichmentRecordsAsync(
        int mediaItemId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var rows = await db.MediaEnrichments
            .Where(e => e.MediaItemId == mediaItemId)
            .ToListAsync(ct);

        return rows.Select(r => new EnrichmentRecord(
            r.PluginId, r.ExternalId, r.Status,
            r.LastCompletedAt, r.ErrorMessage, r.DiagnosticsJson))
            .ToList();
    }

    // ── Background / batch operations ─────────────────────────────────────────
    public async Task EnrichPendingAsync(string pluginId, CancellationToken ct = default)
    {
        // Acquire the per-plugin lock. If another run is already in progress for this plugin,
        // skip immediately rather than queueing a duplicate batch.
        var sem = _pluginLocks.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct))
        {
            logger.LogDebug("EnrichPendingAsync skipped for {PluginId} — run already in progress", pluginId);
            return;
        }
        try
        {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var provider = registry.GetMetadataProvider(pluginId);
        if (provider is null)
        {
            logger.LogWarning("Plugin {PluginId} not found in registry", pluginId);
            return;
        }

        // Build a snapshot of all providers so stub creation can seed enrichment rows for every plugin.
        var allProviders = (registry.GetMetadataProviderEntries() ?? [])
            .Select(e => (e.PluginId, (IMetadataProvider)e.Provider))
            .ToList()
            .AsReadOnly();

        // Expand declared media types to all raw DB name variants so the filter can be
        // pushed into SQL. NormalizeMediaTypeName maps "movies"→"movie" and lowercases;
        // we invert that here so that a plugin declaring "movie" also matches the DB value
        // "movies" (and vice versa) without loading rows into memory to filter them.
        var supportedRawTypes = provider.GetSupportedMediaTypes()
            .SelectMany(t => ExpandMediaTypeName(t.MediaTypeName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Mark any Pending items whose media type this plugin doesn't support as Skipped
        // BEFORE the main loop below ever runs. These rows are permanently excluded by the
        // main loop's own WHERE clause (never eligible, no matter how many passes run), so
        // if this ran only after the loop (as it used to), the loop's own "no eligible rows
        // in this batch but Pending rows remain" retry-forever guard (below) would spin
        // indefinitely — anyRemainingPending stays true forever for a plugin with any
        // permanently-unsupported Pending rows (e.g. audiobooks queued against a movie/TV-only
        // provider), since those rows can never become eligible and the cleanup that would
        // clear them out was unreachable code after a loop that never exits.
        await MarkUnsupportedPendingAsSkippedAsync(db, pluginId, supportedRawTypes, ct);

        var cutoff = DateTime.UtcNow - RetryWindow;

        // Loop until no more eligible items remain.
        // Required for hierarchical content: pass 1 resolves shows, pass 2 seasons, pass 3 episodes.
        // The blocked-parent set is re-fetched on every pass so newly-completed parents unblock
        // their children in the next pass without waiting for a separate ID-list query per row.
        int passNumber = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Pre-fetch the set of item IDs whose enrichment row is still Pending (not yet
            // attempted this run). Child items whose parent is in this set are deferred —
            // the parent gets processed first, then the child on the very next pass.
            //
            // We deliberately only block on Pending parents, NOT on Failed ones. Once a parent
            // has been attempted — even if it failed — its children are eligible immediately.
            // This means the entire hierarchy drains in a single run: level-0 items are tried
            // first (they have no parent and are always eligible), and as each level resolves
            // (Completed, Failed, NotFound, Exhausted), the next level unlocks. Children of
            // failed parents still get their own shot at matching via title/name search.
            //
            // The old behaviour (block on Failed-within-retryWindow too) caused tracks under
            // a failed album to sit blocked for 24 h per retry cycle, taking multiple daily
            // runs to fully drain a large music library.
            var blockedParentIds = (await db.MediaEnrichments
                .Where(e => e.PluginId == pluginId &&
                            e.Status == EnrichmentStatus.Pending)
                .Select(e => e.MediaItemId)
                .ToListAsync(ct))
                .ToHashSet();

            var rows = await db.MediaEnrichments
                .Include(x => x.MediaItem)
                    .ThenInclude(m => m!.MediaType)
                .Include(x => x.MediaItem)
                    .ThenInclude(m => m!.Parent)
                        .ThenInclude(p => p!.Parent)
                .Where(x => x.PluginId == pluginId &&
                            (x.Status == EnrichmentStatus.Pending ||
                             (x.Status == EnrichmentStatus.Failed &&
                              (x.LastAttemptedAt == null || x.LastAttemptedAt < cutoff))) &&
                            // Restrict to supported media types in SQL.
                            (supportedRawTypes.Count == 0 ||
                             (x.MediaItem!.MediaType != null &&
                              supportedRawTypes.Contains(x.MediaItem!.MediaType!.Name))))
                .OrderBy(x => x.MediaItem!.HierarchyLevel)
                .Take(500)
                .ToListAsync(ct);

            // Defer children whose parent hasn't been attempted yet (still Pending).
            // Root items (ParentId == null) are always eligible.
            // Items whose parent has NO enrichment row at all are also eligible — the
            // parent simply doesn't need enrichment from this plugin.
            // Items whose parent was attempted but failed are also eligible — they get
            // their own shot rather than waiting for multiple daily retry cycles.
            rows = rows
                .Where(x => x.MediaItem!.ParentId == null ||
                             !blockedParentIds.Contains(x.MediaItem!.ParentId.Value))
                .ToList();

            if (rows.Count == 0)
            {
                // Re-verify there are genuinely no more Pending rows for this plugin before
                // exiting. Guards against a concurrent operation (file scanner, inbound sync,
                // or MovieCollectionService creating a new collection container) adding Pending
                // rows after the last pass's query but before it finished processing.
                // With blockedParentIds containing only Pending items and ordering by
                // HierarchyLevel, level-0 items (no parent) are always eligible — so the only
                // way rows.Count can be 0 here is if there truly are no Pending items left, or
                // if new rows were added concurrently right as the last pass completed.
                // If any Pending rows remain, do one more pass to catch them.
                var anyRemainingPending = await db.MediaEnrichments
                    .AnyAsync(x => x.PluginId == pluginId &&
                                   x.Status == EnrichmentStatus.Pending, ct);
                if (!anyRemainingPending) break; // Truly done.
                // else: there are Pending rows — at least one more pass is warranted.
                // Increment passNumber so log output shows we're re-entering.
                passNumber++;
                logger.LogDebug(
                    "EnrichPendingAsync pass {Pass}: no eligible rows in this batch but Pending rows remain — " +
                    "retrying for newly-added or unblocked items for plugin {PluginId}",
                    passNumber, pluginId);
                continue;
            }

            passNumber++;
            logger.LogInformation(
                "EnrichPendingAsync pass {Pass}: enriching {Count} items for plugin {PluginId}",
                passNumber, rows.Count, pluginId);

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                // Cascade: false — this loop already walks the full hierarchy itself via its
                // own parent-then-child ordered passes, so recursing into cascade too would
                // process every child twice.
                await EnrichItemCoreAsync(db, provider, pluginId, row,
                    new EnrichmentOptions(EnrichmentMode.FillGaps, Cascade: false), ct, allProviders);
            }

            // All items in this pass have been saved. Clear the change tracker so that
            // the next pass's ToListAsync starts with a clean slate — otherwise tracked
            // entities accumulate across passes (and across all items in each pass),
            // causing SaveChangesAsync to scan an ever-growing set of tracked objects.
            // For a 38k-item music library processed in 3 passes this would otherwise
            // hold ~114k entities in memory by the time the last pass completes.
            db.ChangeTracker.Clear();
        }

        // ── Post-run diagnostics and cleanup ─────────────────────────────────────

        // Safety net: catch any item whose media type changed (e.g. via Change Type) to
        // something this plugin doesn't support while this run was in progress. The real
        // guard against the infinite-loop case is the identical call before the main loop.
        await MarkUnsupportedPendingAsSkippedAsync(db, pluginId, supportedRawTypes, ct);

        // 2. Diagnostic summary — log how many items are still Pending after the run
        //    and break them down by reason so the cause is visible in logs.
        var stillPending = await db.MediaEnrichments
            .Where(x => x.PluginId == pluginId && x.Status == EnrichmentStatus.Pending)
            .CountAsync(ct);

        if (stillPending > 0)
        {
            // Parent still unresolved (Pending or Failed within the retry window) — expected,
            // those items will become eligible once the parent is retried.
            var parentBlocked = await db.MediaEnrichments
                .Where(x => x.PluginId == pluginId &&
                            x.Status == EnrichmentStatus.Pending &&
                            x.MediaItem!.ParentId != null &&
                            db.MediaEnrichments.Any(p =>
                                p.MediaItemId == x.MediaItem!.ParentId &&
                                p.PluginId == pluginId &&
                                p.Status != EnrichmentStatus.Completed &&
                                p.Status != EnrichmentStatus.NotFound  &&
                                p.Status != EnrichmentStatus.Exhausted &&
                                p.Status != EnrichmentStatus.Skipped))
                .CountAsync(ct);

            logger.LogInformation(
                "EnrichPendingAsync complete for {PluginId}: {Passes} pass(es), " +
                "{StillPending} items still Pending " +
                "({ParentBlocked} waiting for parent to resolve, {Other} other — check enrichment drill-down)",
                pluginId, passNumber, stillPending, parentBlocked, stillPending - parentBlocked);
        }
        else if (passNumber > 0)
        {
            logger.LogInformation(
                "EnrichPendingAsync complete for {PluginId}: {Passes} pass(es), all items resolved",
                pluginId, passNumber);
        }
        else
        {
            logger.LogDebug("EnrichPendingAsync: no eligible items for plugin {PluginId}", pluginId);
        }
        } // end try
        finally { sem.Release(); }
    }

    public async Task ResyncAllForPluginAsync(string pluginId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        await using var registryScope = scopeFactory.CreateAsyncScope();
        var registry = registryScope.ServiceProvider.GetRequiredService<IPluginRegistry>();
        var provider = registry.GetMetadataProvider(pluginId);
        var supportedTypes = provider?.GetSupportedMediaTypes()
            .Select(t => NormalizeMediaTypeName(t.MediaTypeName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var libraryItemIds = await db.UserLibraries
            .Select(ul => ul.MediaItemId)
            .Distinct()
            .ToListAsync(ct);

        var rootItems = await db.MediaItems
            .Include(m => m.MediaType)
            .Where(m => libraryItemIds.Contains(m.Id) && m.HierarchyLevel == 0)
            .ToListAsync(ct);

        var rootIds = supportedTypes is { Count: > 0 }
            ? rootItems
                .Where(m => supportedTypes.Contains(NormalizeMediaTypeName(m.MediaType?.Name ?? string.Empty)))
                .Select(m => m.Id)
                .ToList()
            : rootItems.Select(m => m.Id).ToList();

        logger.LogInformation(
            "ResyncAllForPlugin {PluginId}: force-refreshing {Count} root items",
            pluginId, rootIds.Count);

        foreach (var id in rootIds)
        {
            ct.ThrowIfCancellationRequested();
            await EnrichItemAsync(id, pluginId,
                new EnrichmentOptions(EnrichmentMode.Force, Cascade: true), ct);
        }
    }

    public async Task EnrichAllAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();
        var pluginIds = registry.GetMetadataProviderEntries().Select(e => e.PluginId).ToList();
        foreach (var id in pluginIds)
        {
            ct.ThrowIfCancellationRequested();
            await EnrichPendingAsync(id, ct);
        }
    }

    public async Task SeedEnrichmentRowsFromExternalIdsAsync(CancellationToken ct = default)
    {
        await using var svc = scopeFactory.CreateAsyncScope();
        var db = svc.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Build a short-suffix → canonical-plugin-id map from installed plugins.
        // Handles both old short-form sources ("musicbrainz") and new full-form
        // ("chronicle.plugin.musicbrainz") that may appear in media_external_ids.Source.
        var installedPluginIds = await db.Plugins
            .Where(p => p.IsEnabled)
            .Select(p => p.PluginId)
            .ToListAsync(ct);

        var shortToFull = installedPluginIds
            .GroupBy(pid => PluginIdHelper.ToSource(pid), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        string CanonicalPluginId(string source)
        {
            // Already a full canonical ID (present in installed list verbatim)
            if (installedPluginIds.Contains(source, StringComparer.OrdinalIgnoreCase))
                return source;
            // Try mapping short suffix → full ID
            var suffix = PluginIdHelper.ToSource(source);
            return shortToFull.GetValueOrDefault(suffix, source);
        }

        // ── Startup cleanup: remove Skipped rows for unsupported media types ─────────────
        // These accumulate when enrichment runs against an item whose type the plugin doesn't
        // support. The enrichment service writes the exact message below when it skips; we
        // use that as a reliable discriminator so this cleanup doesn't depend on the plugin
        // registry being fully loaded yet (plugins load asynchronously after startup seeding).
        var staleSkipped = await db.MediaEnrichments
            .Where(me => me.Status == EnrichmentStatus.Skipped
                      && me.ErrorMessage != null
                      && me.ErrorMessage.Contains("is not supported by plugin"))
            .ToListAsync(ct);
        if (staleSkipped.Count > 0)
        {
            db.MediaEnrichments.RemoveRange(staleSkipped);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "SeedEnrichmentRows: pruned {Count} Skipped rows for unsupported media types",
                staleSkipped.Count);
        }

        // NOTE: there used to be a second cleanup pass here that deleted enrichment rows for
        // "import provider" plugins (SIMKL, Trakt) whenever an item had no external ID from
        // that plugin yet, on the premise that "those plugins can only enrich items they
        // already know about." That premise is false for any import provider that ALSO
        // implements IMetadataProvider with a real SearchAsync (Trakt and SIMKL both do — they
        // search by title against their own catalogs, exactly like TMDB) — Phase 3 below
        // already has the single correct rule for exactly this ("combined plugins get
        // title-search enrichment same as any metadata provider"). Having both meant every
        // restart deleted a batch of rows here and Phase 3 immediately recreated the identical
        // batch — pure wasted work, re-litigating the same decision in two places that
        // disagreed. Phase 3's inclusion rule is now the only place that decision is made.
        var pluginRegistry = svc.ServiceProvider
            .GetRequiredService<Chronicle.Services.Plugins.IPluginRegistry>();

        // Build plugin → supported media type names map for Phase 1 type filtering.
        // This prevents creating enrichment rows for items whose type the plugin doesn't
        // support (e.g. TMDB rows for music items that happen to have a stale tmdb ExternalId).
        var pluginSupportedTypes = pluginRegistry
            .GetMetadataProviderEntries()
            .ToDictionary(
                e => e.PluginId,
                e => e.Provider.GetSupportedMediaTypes()
                         .Select(t => NormalizeMediaTypeName(t.MediaTypeName))
                         .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        // Load all existing enrichment rows (for deduplication)
        var enrichmentSet = (await db.MediaEnrichments
            .Select(me => new { me.MediaItemId, PluginId = me.PluginId.ToLower() })
            .ToListAsync(ct))
            .Select(e => (e.MediaItemId, e.PluginId))
            .ToHashSet();

        // Candidates: external IDs mapped to canonical plugin IDs, deduplicated.
        // Filter to known plugin sources in SQL to avoid loading the entire table.
        var knownSources = shortToFull.Keys.ToList();
        var candidates = (await db.Set<MediaExternalId>()
            .Where(mei => mei.ExternalId != "__suppress__" && knownSources.Contains(mei.Source))
            .ToListAsync(ct))
            .Select(mei => (MediaItemId: mei.MediaItemId,
                            PluginId:    CanonicalPluginId(mei.Source),
                            ExternalId:  mei.ExternalId))
            // Skip collection-container ExternalIds — those are metadata about a container,
            // not an identifier for a media item to enrich. Enriching containers as movies
            // causes wrong title matches and bad re-parenting of real movies into unrelated containers.
            .Where(c => !c.ExternalId.StartsWith("collection:", StringComparison.OrdinalIgnoreCase))
            .Where(c => !enrichmentSet.Contains((c.MediaItemId, c.PluginId.ToLower())))
            .GroupBy(c => (c.MediaItemId, c.PluginId))   // dedup same item+plugin
            .Select(g => g.First())
            .ToList();

        if (candidates.Count == 0) return;

        // Load MetadataJson and media type names for all affected items, chunked to stay
        // under SQLite's SQLITE_LIMIT_VARIABLE_NUMBER=999 limit.
        var itemIds = candidates.Select(c => c.MediaItemId).Distinct().ToList();
        var metadataByItem  = new Dictionary<int, string?>(itemIds.Count);
        var mediaTypeByItem = new Dictionary<int, string>(itemIds.Count);
        foreach (var chunk in itemIds.Chunk(500))
        {
            var chunkData = await db.MediaItems
                .Include(mi => mi.MediaType)
                .Where(mi => chunk.Contains(mi.Id))
                .Select(mi => new { mi.Id, mi.MetadataJson, TypeName = mi.MediaType!.Name })
                .ToListAsync(ct);
            foreach (var row in chunkData)
            {
                metadataByItem[row.Id]  = row.MetadataJson;
                mediaTypeByItem[row.Id] = row.TypeName;
            }
        }

        // For each candidate: Completed if plugin data is intact in MetadataJson,
        // Pending if data is absent (wiped by re-scan) so it re-enriches automatically.
        // Skip candidates whose item type is not supported by the plugin — this prevents
        // stale ExternalIds (e.g. a tmdb source on a music item) from creating useless rows.
        int completedCount = 0, pendingCount = 0;
        var toAdd = new List<MediaItemEnrichment>(candidates.Count);
        foreach (var (mediaItemId, pluginId, externalId) in candidates)
        {
            // Fail closed, not open: only proceed on positive proof the plugin supports this
            // item's type. If the plugin isn't currently registered (declares zero types —
            // e.g. this ran before the plugin registry finished loading) or the item's type
            // can't be resolved, skip rather than assume it's fine — the previous "only skip
            // on positive proof of MISmatch" version silently let every candidate through
            // whenever the registry lookup failed for any reason, which is exactly how stale
            // media_external_ids rows (e.g. a music track's stray SIMKL ID) kept resurrecting
            // mismatched-type enrichment rows on every restart.
            if (!pluginSupportedTypes.TryGetValue(pluginId, out var supportedForPlugin)
                || supportedForPlugin.Count == 0)
                continue;
            if (!mediaTypeByItem.TryGetValue(mediaItemId, out var itemType))
                continue;
            if (!supportedForPlugin.Contains(NormalizeMediaTypeName(itemType)))
                continue;   // plugin doesn't support this item's type — skip

            var json   = metadataByItem.GetValueOrDefault(mediaItemId);
            var status = HasPluginDataInJson(json, pluginId)
                ? EnrichmentStatus.Completed
                : EnrichmentStatus.Pending;

            if (status == EnrichmentStatus.Completed) completedCount++;
            else                                       pendingCount++;

            toAdd.Add(new MediaItemEnrichment
            {
                MediaItemId = mediaItemId,
                PluginId    = pluginId,
                ExternalId  = externalId,
                Status      = status,
                RetryCount  = 0,
                MaxRetries  = 3,
            });
        }

        if (toAdd.Count > 0)
        {
            db.MediaEnrichments.AddRange(toAdd);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "SeedEnrichmentRows: created {Completed} Completed + {Pending} Pending rows from media_external_ids",
                completedCount, pendingCount);
        }

        // ── Phase 2: reset "stuck" rows ───────────────────────────────────────
        // Completed or Exhausted rows where MetadataJson has no plugin data are
        // broken — either they were seeded incorrectly or data was wiped and the
        // row was never cleared. Reset them to Pending so they re-enrich on the
        // next background pass.
        //
        // NotFound is intentionally EXCLUDED: it is a valid terminal state meaning
        // "the provider was reached but returned no matching result".  Resetting
        // NotFound rows here would create an infinite loop — the provider would
        // return NotFound again, the startup reset would fire again, ad infinitum.
        // Users can manually reset NotFound rows from the Enrichment drill-down page.
        var stuckRows = await db.MediaEnrichments
            .Include(me => me.MediaItem)
            .Where(me => me.Status == EnrichmentStatus.Completed ||
                         me.Status == EnrichmentStatus.Exhausted)
            .ToListAsync(ct);

        int resetCount = 0;
        foreach (var row in stuckRows)
        {
            if (HasPluginDataInJson(row.MediaItem?.MetadataJson, row.PluginId)) continue;
            row.Status     = EnrichmentStatus.Pending;
            row.RetryCount = 0;
            row.ErrorMessage = null;
            resetCount++;
        }

        if (resetCount > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "SeedEnrichmentRows: reset {Count} Completed/Exhausted rows with no plugin data to Pending",
                resetCount);
        }

        // ── Phase 3: seed missing enrichment rows for supported types ─────────────
        // When a new plugin is installed (or a plugin adds support for a new media type),
        // existing items of that type have no enrichment row for the plugin.
        // This pass creates Pending rows so the background enrichment service picks them up.
        // Critically, this also fixes fan edit items that had their enrichment rows deleted
        // by ChangeTypeAsync before the row-seeding fix was in place.
        //
        // Only plugins registered as IMetadataProvider (from GetMetadataProviderEntries) are
        // included here. Import-only plugins (Trakt, SIMKL) implement IImportProvider with
        // enrichment hooks but do NOT appear in GetMetadataProviderEntries, so they are
        // naturally excluded. A combined plugin (e.g. Hardcover) that implements both
        // IImportProvider and IMetadataProvider IS included — it can enrich any item by
        // title search, just like a pure metadata provider.
        var pluginEntries = pluginRegistry.GetMetadataProviderEntries().ToList();

        if (pluginEntries.Count > 0)
        {
            // Load items with their media type names
            var allItems = await db.MediaItems
                .Include(m => m.MediaType)
                .Where(m => m.MediaType != null)
                .Select(m => new { m.Id, TypeName = m.MediaType!.Name })
                .ToListAsync(ct);

            var itemTypeMap = allItems.ToDictionary(i => i.Id, i => i.TypeName);

            // ── Phase 3b: prune Pending rows for unsupported type/plugin pairs ─────
            // Pending rows for types a plugin doesn't support will always be Skipped
            // when the enrichment service picks them up — delete them proactively now
            // that we have the full plugin registry loaded. This catches rows seeded
            // by old code before the type-filter was enforced.
            int phase3bDeletedTotal = 0;
            foreach (var (pluginId, provider, _) in pluginEntries)
            {
                var supportedTypes = provider.GetSupportedMediaTypes()
                    .Select(t => NormalizeMediaTypeName(t.MediaTypeName))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var pendingForPlugin = await db.MediaEnrichments
                    .Where(me => me.PluginId == pluginId && me.Status == EnrichmentStatus.Pending)
                    .ToListAsync(ct);

                var toDelete = pendingForPlugin
                    .Where(me => itemTypeMap.TryGetValue(me.MediaItemId, out var typeName)
                                 && !supportedTypes.Contains(NormalizeMediaTypeName(typeName)))
                    .ToList();

                if (toDelete.Count > 0)
                {
                    db.MediaEnrichments.RemoveRange(toDelete);
                    phase3bDeletedTotal += toDelete.Count;
                }
            }
            if (phase3bDeletedTotal > 0)
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "SeedEnrichmentRows Phase 3b: pruned {Count} stale Pending rows for unsupported media types",
                    phase3bDeletedTotal);
            }

            // Existing enrichment rows (as a set for O(1) lookup) — re-read after pruning
            var existingRows = (await db.MediaEnrichments
                .Select(me => new { me.MediaItemId, me.PluginId })
                .ToListAsync(ct))
                .Select(r => (r.MediaItemId, r.PluginId.ToLower()))
                .ToHashSet();

            var phase3ToAdd = new List<MediaItemEnrichment>();
            foreach (var (pluginId, provider, _) in pluginEntries)
            {
                var supportedTypes = provider.GetSupportedMediaTypes()
                    .Select(t => NormalizeMediaTypeName(t.MediaTypeName))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var item in allItems)
                {
                    if (!supportedTypes.Contains(NormalizeMediaTypeName(item.TypeName)))
                        continue;
                    if (existingRows.Contains((item.Id, pluginId.ToLower())))
                        continue;

                    phase3ToAdd.Add(new MediaItemEnrichment
                    {
                        MediaItemId = item.Id,
                        PluginId    = pluginId,
                        Status      = EnrichmentStatus.Pending,
                        MaxRetries  = 3,
                    });
                    existingRows.Add((item.Id, pluginId.ToLower())); // prevent dups within this pass
                }
            }

            if (phase3ToAdd.Count > 0)
            {
                db.MediaEnrichments.AddRange(phase3ToAdd);
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "SeedEnrichmentRows Phase 3: created {Count} missing Pending rows for newly-supported items",
                    phase3ToAdd.Count);
            }
        }
    }

    /// <summary>
    /// Returns true if <paramref name="json"/> contains a top-level property for the given plugin.
    /// Checks both the full plugin ID (e.g. "chronicle.plugin.musicbrainz") and the short suffix
    /// ("musicbrainz") to handle data written by older code versions.
    /// </summary>
    private static bool HasPluginDataInJson(string? json, string pluginId)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty(pluginId, out _)) return true;
            // Also check short-form key written by older code (e.g. "musicbrainz")
            var shortId = pluginId.Contains('.') ? PluginIdHelper.ToSource(pluginId) : null;
            if (shortId is not null && root.TryGetProperty(shortId, out _)) return true;
            return false;
        }
        catch { return false; }
    }

    public async Task ResetAsync(string pluginId, ResetScope scope, int? mediaItemId = null, CancellationToken ct = default)
    {
        await using var svc = scopeFactory.CreateAsyncScope();
        var db = svc.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        IQueryable<MediaItemEnrichment> query = db.MediaEnrichments
            .Where(x => x.PluginId == pluginId);

        query = scope switch
        {
            ResetScope.Single       => query.Where(x => x.MediaItemId == mediaItemId),
            ResetScope.AllFailed    => query.Where(x => x.Status == EnrichmentStatus.Failed),
            ResetScope.AllExhausted => query.Where(x => x.Status == EnrichmentStatus.Exhausted),
            ResetScope.AllNotFound  => query.Where(x => x.Status == EnrichmentStatus.NotFound),
            ResetScope.AllSkipped   => query.Where(x => x.Status == EnrichmentStatus.Skipped),
            ResetScope.AllAuthFailed => query.Where(x => x.Status == EnrichmentStatus.AuthFailed),
            ResetScope.AllForPlugin => query.Where(x => x.Status != EnrichmentStatus.Skipped),
            _                       => query
        };

        // Load entities and update in memory to stay compatible with the EF InMemory provider
        // (which does not support ExecuteUpdateAsync bulk operations).
        var rows = await query.ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status          = EnrichmentStatus.Pending;
            row.RetryCount      = 0;
            row.ErrorMessage    = null;
            row.LastAttemptedAt = null;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SkipAsync(int mediaItemId, string pluginId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var row = await db.MediaEnrichments
            .FirstOrDefaultAsync(x => x.MediaItemId == mediaItemId && x.PluginId == pluginId, ct);
        if (row is not null)
        {
            row.Status = EnrichmentStatus.Skipped;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<EnrichmentStats>> GetStatsAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        // Only show plugins that are actually registered as metadata providers.
        // Plugins like the file scanner are enabled but have no metadata provider,
        // so they must not appear in the enrichment stats table.
        var metadataPluginIds = registry.GetMetadataProviderEntries()
            .Select(e => e.PluginId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var metadataPlugins = await db.Plugins
            .Where(p => p.IsEnabled && metadataPluginIds.Contains(p.PluginId))
            .ToListAsync(ct);

        // Enrichment counts grouped by plugin ID
        var rows = await db.MediaEnrichments
            .GroupBy(x => x.PluginId)
            .Select(g => new
            {
                PluginId   = g.Key,
                Pending    = g.Count(x => x.Status == EnrichmentStatus.Pending),
                Completed  = g.Count(x => x.Status == EnrichmentStatus.Completed),
                Failed     = g.Count(x => x.Status == EnrichmentStatus.Failed),
                Exhausted  = g.Count(x => x.Status == EnrichmentStatus.Exhausted),
                NotFound   = g.Count(x => x.Status == EnrichmentStatus.NotFound),
                Skipped    = g.Count(x => x.Status == EnrichmentStatus.Skipped),
                AuthFailed = g.Count(x => x.Status == EnrichmentStatus.AuthFailed),
            })
            .ToListAsync(ct);

        var rowLookup = rows.ToDictionary(r => r.PluginId, StringComparer.OrdinalIgnoreCase);

        // Return one entry per installed metadata plugin, defaulting to zeros
        return metadataPlugins
            .Select(p =>
            {
                rowLookup.TryGetValue(p.PluginId, out var r);
                return new EnrichmentStats(
                    p.PluginId,
                    p.Name,
                    r?.Pending    ?? 0,
                    r?.Completed  ?? 0,
                    r?.Failed     ?? 0,
                    r?.Exhausted  ?? 0,
                    r?.NotFound   ?? 0,
                    r?.Skipped    ?? 0,
                    r?.AuthFailed ?? 0);
            })
            .ToList();
    }

    public async Task<PagedEnrichmentItems> GetItemsAsync(
        string pluginId, string? status, int page, int pageSize, string? search,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        IQueryable<MediaItemEnrichment> query = db.MediaEnrichments
            .Include(x => x.MediaItem)
                .ThenInclude(m => m!.MediaType)
            .Include(x => x.MediaItem)
                .ThenInclude(m => m!.Parent)
                    .ThenInclude(p => p!.Parent)
            .Where(x => x.PluginId == pluginId);

        if (!string.IsNullOrEmpty(status) &&
            Enum.TryParse<EnrichmentStatus>(status, ignoreCase: true, out var parsedStatus))
            query = query.Where(x => x.Status == parsedStatus);

        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(x =>
                // Title
                (x.MediaItem != null && EF.Functions.Like(x.MediaItem.Name, pattern)) ||
                // MetadataJson blob — covers author, series, file paths stored by the scanner
                (x.MediaItem != null && x.MediaItem.MetadataJson != null &&
                 EF.Functions.Like(x.MediaItem.MetadataJson, pattern)) ||
                // Stored external ID (e.g. "release-group:xxxx")
                (x.ExternalId != null && EF.Functions.Like(x.ExternalId, pattern)) ||
                // Parent name (artist for music, show for TV)
                (x.MediaItem != null && x.MediaItem.Parent != null &&
                 EF.Functions.Like(x.MediaItem.Parent.Name, pattern)));
        }

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderBy(x => x.MediaItem != null ? x.MediaItem.Name : string.Empty)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = rows.Select(row =>
        {
            string? scannerJson = null;
            if (row.MediaItem?.MetadataJson is { } mj)
            {
                try
                {
                    using var doc = JsonDocument.Parse(mj);
                    if (doc.RootElement.TryGetProperty("fileScanner", out var fs))
                        scannerJson = fs.GetRawText();
                }
                catch { /* ignore */ }
            }

            return new EnrichmentItemResult(
                row.Id,
                row.MediaItemId,
                row.MediaItem?.Name ?? "(unknown)",
                row.MediaItem?.Year,
                row.MediaItem?.MediaType?.DisplayName ?? row.MediaItem?.MediaType?.Name ?? "Unknown",
                row.MediaItem?.HierarchyLevel ?? 0,
                row.MediaItem?.PosterUrl,
                row.ExternalId,
                row.Status,
                row.ErrorMessage,
                row.RetryCount,
                row.MaxRetries,
                row.LastAttemptedAt,
                row.DiagnosticsJson,
                scannerJson,
                row.MediaItem?.Parent?.Name,
                row.MediaItem?.Parent?.Parent?.Name);
        }).ToList();

        return new PagedEnrichmentItems(items, total, page, pageSize);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    // Per-(item, plugin) lock: prevents a batch pass and a single-item refresh/Fix-Match
    // from racing on the exact same enrichment row. The per-plugin semaphore above only
    // stops two batch runs from overlapping; it does nothing for a single-item call that
    // happens to touch an item the batch pass is also mid-way through. Whichever finished
    // last used to silently win the DB write with no coordination at all — this serializes
    // any two callers touching the same (item, plugin) pair instead of letting them race.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int MediaItemId, string PluginId), SemaphoreSlim>
        _itemPluginLocks = new();

    private const int DefaultConfidenceThreshold = 50;

    // How long a caller will wait to acquire the per-(item, plugin) lock below before giving up.
    // Must be longer than ProviderCallGuard.DefaultTimeout (the lock is held for the duration of
    // one provider call) with headroom for the rest of EnrichItemCoreLockedAsync's own DB work.
    private static readonly TimeSpan ItemLockTimeout = TimeSpan.FromSeconds(40);

    /// <summary>
    /// Enriches a single (item, plugin) enrichment row. This is the one canonical
    /// implementation used by every caller — the batch pass (<see cref="EnrichPendingAsync"/>),
    /// single-item refresh/Fix-Match (<see cref="EnrichItemAsync(int,string,EnrichmentOptions,CancellationToken)"/>),
    /// and cascade-to-children (<see cref="CascadeToChildrenAsync"/>) all funnel through here.
    /// <paramref name="row"/>.MediaItem must already be populated by the caller.
    /// </summary>
    private async Task<MediaMetadata?> EnrichItemCoreAsync(
        ChronicleDbContext db, IMetadataProvider provider, string pluginId,
        MediaItemEnrichment row, EnrichmentOptions options, CancellationToken ct,
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)>? allProviders = null)
    {
        var lockKey = (row.MediaItemId, pluginId);
        var itemSem = _itemPluginLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        // Bounded wait, not WaitAsync(ct) alone: ProviderCallGuard.DefaultTimeout guarantees
        // whoever is CURRENTLY holding this lock releases it within ~25s, but ct itself is frequently
        // CancellationToken.None from fire-and-forget callers (EnrichPendingAsync's batch loop,
        // the enrichment controller's Task.Run) and would otherwise never expire on its own —
        // this is the second half of making sure nothing waiting on a plugin can hang forever.
        var acquired = await itemSem.WaitAsync(ItemLockTimeout, ct);
        if (!acquired)
        {
            logger.LogError(
                "Couldn't acquire enrichment lock for item {ItemId} plugin {PluginId} within {TimeoutS}s " +
                "— another enrichment for this exact (item, plugin) pair is still running or stuck; skipping this attempt",
                row.MediaItemId, pluginId, ItemLockTimeout.TotalSeconds);
            return null;
        }
        try
        {
            return await EnrichItemCoreLockedAsync(db, provider, pluginId, row, options, ct, allProviders);
        }
        finally
        {
            itemSem.Release();
        }
    }

    private async Task<MediaMetadata?> EnrichItemCoreLockedAsync(
        ChronicleDbContext db, IMetadataProvider provider, string pluginId,
        MediaItemEnrichment row, EnrichmentOptions options, CancellationToken ct,
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)>? allProviders)
    {
        var item = row.MediaItem!;
        MediaMetadata? completedMeta = null;

        // FillGaps skip — background/batch behaviour. Single-item callers pass Force to
        // always re-fetch (Refresh button); Fix Match always supplies an IdOverride, which
        // bypasses this regardless of mode. The batch pass's own row-selection query already
        // excludes Completed rows, so this is a no-op from that caller — kept here so the
        // check applies uniformly no matter which caller reaches this method.
        if (options.Mode == EnrichmentMode.FillGaps
            && row.Status == EnrichmentStatus.Completed
            && options.IdOverride is null)
        {
            if (options.Cascade)
                await CascadeToChildrenAsync(db, provider, pluginId, item, options, ct, allProviders);
            return null;
        }

        row.LastAttemptedAt = DateTime.UtcNow;
        string searchQuery = string.Empty;
        List<ScoredCandidate> rawCandidates = [];
        try
        {
            MediaMetadata? result = null;
            string? resolvedId = null;

            // ── Step 0: Fix Match override ─────────────────────────────────────────
            // User-supplied external ID bypasses scoring entirely. A bare show-level ID
            // (e.g. "tv:63197") entered for a season/episode item must be promoted to a
            // compound ID using the item's position in the hierarchy — otherwise
            // GetByIdAsync returns show-level data applied to a season/episode.
            if (options.IdOverride is not null)
            {
                resolvedId = options.IdOverride.Trim();

                if (item.ParentId is not null
                    && resolvedId.StartsWith("tv:", StringComparison.OrdinalIgnoreCase)
                    && !resolvedId.Contains('/'))
                {
                    var showTmdbId = resolvedId.Split(':', 2)[1];
                    if (item.HierarchyLevel >= 2 && item.Parent is not null)
                    {
                        int? seasonNum = item.Parent.Number;
                        if (seasonNum is null && item.Parent.Name is { } pn)
                        {
                            var m2 = System.Text.RegularExpressions.Regex.Match(pn, @"\d+");
                            if (m2.Success) seasonNum = int.Parse(m2.Value);
                        }
                        int? epNum = item.Number;
                        if (epNum is null && item.Name is { } en)
                        {
                            var m3 = System.Text.RegularExpressions.Regex.Match(en, @"\d+");
                            if (m3.Success) epNum = int.Parse(m3.Value);
                        }
                        if (seasonNum.HasValue && epNum.HasValue)
                        {
                            resolvedId = $"tv:{showTmdbId}/season:{seasonNum}/episode:{epNum}";
                            logger.LogInformation(
                                "Fix Match: promoted bare show ID to episode compound ID {Id} for item {ItemId}",
                                resolvedId, item.Id);
                        }
                    }
                    else if (item.HierarchyLevel == 1)
                    {
                        int? seasonNum = item.Number;
                        if (seasonNum is null && item.Name is { } sn)
                        {
                            var m2 = System.Text.RegularExpressions.Regex.Match(sn, @"\d+");
                            if (m2.Success) seasonNum = int.Parse(m2.Value);
                        }
                        if (seasonNum.HasValue)
                        {
                            resolvedId = $"tv:{showTmdbId}/season:{seasonNum}";
                            logger.LogInformation(
                                "Fix Match: promoted bare show ID to season compound ID {Id} for item {ItemId}",
                                resolvedId, item.Id);
                        }
                    }
                }

                result = await ProviderCallGuard.CallAsync<MediaMetadata?>(
                    t => provider.GetByIdAsync(resolvedId, t), provider.PluginId, "GetByIdAsync", null, msg => logger.LogWarning("{Msg}", msg), msg => logger.LogError("{Msg}", msg), ct);
            }

            // Force mode (user-triggered Refresh / Refresh All): don't trust a previously
            // stored ID at face value — clear it so Steps 2-5 below run a genuine fresh
            // derivation/search instead of just re-confirming the same ID. This lets a
            // stale or wrong match (e.g. from a scoring bug that has since been fixed)
            // self-correct on refresh instead of being reused forever. If the fresh
            // attempt below finds nothing, the original ID/Completed status is restored
            // in the not-found branch rather than downgrading a working match to NotFound.
            var forceRefreshOriginalId = options.Mode == EnrichmentMode.Force ? row.ExternalId : null;
            var forceRefreshHadMatch = options.Mode == EnrichmentMode.Force
                && options.IdOverride is null
                && row.Status == EnrichmentStatus.Completed
                && !string.IsNullOrEmpty(row.ExternalId);
            if (forceRefreshHadMatch)
                row.ExternalId = null;

            // ── Step 1: Validate any stored ExternalId ────────────────────────────
            // Clear IDs whose entity type doesn't match the item's hierarchy level.
            // This handles previously-wrong enrichments (e.g. a bare "tv:63197" stored
            // on an episode item from an earlier name-search that matched the wrong show).
            // After clearing, Step 2 will derive the correct ID from the show hierarchy.
            // Skipped when Step 0 already resolved an explicit Fix Match override, or
            // when Force mode just cleared the ID above to force a fresh derivation/search.
            if (options.IdOverride is null && !string.IsNullOrEmpty(row.ExternalId) && row.MediaItem is not null)
            {
                bool idIsValid = true;
                var sep = row.ExternalId.IndexOf(':');
                if (sep > 0)
                {
                    var entityType = row.ExternalId[..sep];

                    // The hierarchy validity check only applies to TMDB-format IDs
                    // ("movie:N", "tv:N", "tv:N/season:M", etc.) and MusicBrainz artist IDs.
                    // Plugin-specific IDs (simkl:*, trakt:*, tvdb:*, hardcover:*, etc.) are
                    // trusted as-is — they were seeded by the plugin's own logic and don't
                    // need a level/type consistency check.
                    bool isTmdbOrMusicBrainzFormat =
                        entityType is "movie" or "tv" or "artist" or "release-group" or "release"
                                    or "season" or "album" or "recording" or "episode";

                    if (isTmdbOrMusicBrainzFormat && row.MediaItem.ParentId == null)
                    {
                        // Root item — TMDB/MusicBrainz format: must be artist, movie, or show-level tv:N
                        idIsValid = entityType is "artist" or "movie" or "tv";
                    }
                    else if (isTmdbOrMusicBrainzFormat)
                    {
                        var parent = await db.MediaItems
                            .AsNoTracking()
                            .FirstOrDefaultAsync(m => m.Id == row.MediaItem.ParentId, ct);
                        if (parent?.ParentId == null)
                        {
                            // This could be: season/album level OR a movie under a collection.
                            // Check if the parent is a movies-type collection (HierarchyLevel 0, same MediaType as a movie).
                            // If so, a "movie" entity type is valid at this depth.
                            bool parentIsMovieCollection = false;
                            if (entityType == "movie" && parent is not null)
                            {
                                var parentMediaType = await db.MediaTypes
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(t => t.Id == parent.MediaTypeId, ct);
                                var pName = parentMediaType?.Name;
                                parentIsMovieCollection =
                                    string.Equals(pName, "movies",   StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(pName, "fanedits", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(pName, "anime",    StringComparison.OrdinalIgnoreCase);
                            }

                            if (parentIsMovieCollection)
                            {
                                idIsValid = true; // movie under a collection — valid
                            }
                            else if (entityType == "tv")
                            {
                                idIsValid = row.ExternalId.Contains("/season:", StringComparison.OrdinalIgnoreCase)
                                         || row.ExternalId.Contains(":s", StringComparison.OrdinalIgnoreCase);
                            }
                            else
                            {
                                idIsValid = entityType is "release-group" or "season" or "album";
                            }
                        }
                        else
                        {
                            // Episode/track level — MusicBrainz "recording:" or TMDB "tv:N/season:N/episode:N"
                            idIsValid = entityType is "recording" or "episode"
                                || (entityType == "tv" && row.ExternalId.Contains("/episode:", StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }

                // For hierarchical items, verify the stored ID's show portion matches the
                // parent's show ID. Catches a child enriched against the wrong show (e.g.
                // episodes matched to tv:157239 while the parent season is tv:243129/season:1).
                //
                // Only meaningful for TMDB/MusicBrainz-style IDs, where a child's ID is built
                // by literally appending to the parent's ID string (e.g. "tv:157239/season:1").
                // Plugins with flat, independent ID namespaces per entity type (e.g. Hardcover:
                // book IDs and series IDs are unrelated integer sequences) would have this
                // comparison fail unconditionally for every valid ID — confirmed real bug: a
                // correctly-matched Hardcover book was discarded and re-derived on every single
                // refresh because its ID naturally shares no prefix with its series' ID.
                var storedEntityType = row.ExternalId!.IndexOf(':') is var sep2 && sep2 > 0
                    ? row.ExternalId[..sep2] : null;
                var isHierarchicalIdFormat = storedEntityType is "movie" or "tv" or "artist"
                    or "release-group" or "release" or "season" or "album" or "recording" or "episode";

                if (idIsValid && isHierarchicalIdFormat
                    && row.MediaItem.HierarchyLevel > 0 && row.MediaItem.ParentId is not null)
                {
                    var parentRow = await db.MediaEnrichments
                        .FirstOrDefaultAsync(e => e.MediaItemId == row.MediaItem.ParentId && e.PluginId == pluginId, ct);
                    if (parentRow?.ExternalId is not null)
                    {
                        var storedBase = row.ExternalId.Split('/')[0];
                        var parentBase = parentRow.ExternalId.Split('/')[0];
                        if (!string.Equals(storedBase, parentBase, StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogInformation(
                                "Stored ExternalId {StoredId} for item {ItemId} is inconsistent with parent ({ParentId}); re-deriving",
                                row.ExternalId, row.MediaItemId, parentRow.ExternalId);
                            idIsValid = false;
                        }
                    }
                }

                if (idIsValid)
                {
                    try
                    {
                        result = await ProviderCallGuard.CallAsync<MediaMetadata?>(
                            t => provider.GetByIdAsync(row.ExternalId, t), provider.PluginId, "GetByIdAsync", null, msg => logger.LogWarning("{Msg}", msg), msg => logger.LogError("{Msg}", msg), ct);
                    }
                    catch (ArgumentException ex)
                    {
                        // Malformed or unsupported ExternalId format — clear it so SearchAsync
                        // can find the correct ID on the next attempt.
                        logger.LogWarning(
                            "Clearing malformed ExternalId {ExternalId} for item {ItemId}: {Error}",
                            row.ExternalId, row.MediaItemId, ex.Message);
                        row.ExternalId = null;
                    }
                    catch (KeyNotFoundException ex)
                    {
                        // Wrap in a private sentinel so the outer catch can distinguish
                        // "provider returned no match" from unrelated KeyNotFoundExceptions
                        // thrown by dictionary access or LINQ elsewhere in this method.
                        throw new ProviderNotFoundException(ex.Message, ex);
                    }
                }
                else
                {
                    logger.LogWarning(
                        "Discarding stale ExternalId {ExternalId} for item {ItemId} — " +
                        "entity type does not match hierarchy level; will re-derive.",
                        row.ExternalId, row.MediaItemId);
                    row.ExternalId = null;
                }
            }

            // ── Step 2: TV season/episode hierarchy derivation ────────────────────
            // Runs when there is no valid stored ExternalId (either originally absent
            // or just cleared above).  Constructs the correct TMDB ID from the parent
            // show's enrichment rather than searching by name, which is unreliable.
            //
            // Season:  tv:{showId}/season:{N}
            // Episode: tv:{showId}/season:{S}/episode:{E}
            //   Always use the SHOW's (grandparent's) ExternalId for the bare showId —
            //   the season's ExternalId ("tv:67198/season:5") cannot be split naively.
            bool hierarchyDerivedId = false;
            if (result is null && string.IsNullOrEmpty(row.ExternalId) && row.MediaItem?.ParentId is not null)
            {
                var parentId = row.MediaItem.ParentId;

                var parentItem = await db.MediaItems
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == parentId, ct);

                // Use the item's Number field; fallback: parse from the name ("Season 01" → 1)
                int? itemNumber = row.MediaItem.Number;
                if (itemNumber is null && row.MediaItem.Name is { } nm)
                {
                    var numMatch = System.Text.RegularExpressions.Regex.Match(nm, @"\d+");
                    if (numMatch.Success)
                        itemNumber = int.Parse(numMatch.Value);
                }

                if (parentItem?.ParentId is not null)
                {
                    // ── Episode level: parent=season, grandparent=show ──────────────
                    var showEnrichment = await db.MediaEnrichments
                        .FirstOrDefaultAsync(
                            e => e.MediaItemId == parentItem.ParentId && e.PluginId == row.PluginId,
                            ct);

                    if (showEnrichment?.ExternalId is { } showExternalId
                        && showExternalId.StartsWith("tv:", StringComparison.OrdinalIgnoreCase)
                        && !showExternalId.Contains('/'))
                    {
                        var showTmdbId = showExternalId.Split(':', 2)[1];

                        int? seasonNumber = parentItem.Number;
                        if (seasonNumber is null && parentItem.Name is { } pnm)
                        {
                            var snm = System.Text.RegularExpressions.Regex.Match(pnm, @"\d+");
                            if (snm.Success)
                                seasonNumber = int.Parse(snm.Value);
                        }
                        if (itemNumber.HasValue && seasonNumber.HasValue)
                        {
                            row.ExternalId = $"tv:{showTmdbId}/season:{seasonNumber}/episode:{itemNumber}";
                            hierarchyDerivedId = true;
                            logger.LogInformation(
                                "TV hierarchy lookup (episode): item={ItemId} → {ExternalId}",
                                row.MediaItemId, row.ExternalId);
                        }
                    }
                    // else: show not enriched or no tv: ID — fall through to name search
                }
                else
                {
                    // ── Season level: parent=show ───────────────────────────────────
                    var showEnrichment = await db.MediaEnrichments
                        .FirstOrDefaultAsync(
                            e => e.MediaItemId == parentId && e.PluginId == row.PluginId,
                            ct);

                    if (showEnrichment?.ExternalId is { } showExternalId
                        && showExternalId.StartsWith("tv:", StringComparison.OrdinalIgnoreCase)
                        && !showExternalId.Contains('/'))
                    {
                        var showTmdbId = showExternalId.Split(':', 2)[1];
                        if (itemNumber.HasValue)
                        {
                            row.ExternalId = $"tv:{showTmdbId}/season:{itemNumber}";
                            hierarchyDerivedId = true;
                            logger.LogInformation(
                                "TV hierarchy lookup (season): item={ItemId} → {ExternalId}",
                                row.MediaItemId, row.ExternalId);
                        }
                    }
                    // else: parent not enriched with a tv: ID — fall through to name search
                }

                // If we derived an ID, call the provider now
                if (hierarchyDerivedId && !string.IsNullOrEmpty(row.ExternalId))
                    result = await ProviderCallGuard.CallAsync<MediaMetadata?>(
                        t => provider.GetByIdAsync(row.ExternalId, t), provider.PluginId, "GetByIdAsync", null, msg => logger.LogWarning("{Msg}", msg), msg => logger.LogError("{Msg}", msg), ct);
            }

            // ── NFO sidecar fallback (root items only, TMDB-style plugins) ─────────
            // Before doing a name search, check whether the item's scan folder contains
            // a tvshow.nfo or movie.nfo with a <uniqueid type="tmdb"> element.
            // This handles ambiguous show names (e.g. "What If") where year-based search
            // may still pick the wrong entry — an NFO is an authoritative identifier.
            // Only TMDB-compatible plugins recognise the numeric ID; skip for others.
            if (result is null && string.IsNullOrEmpty(row.ExternalId)
                && row.MediaItem?.ParentId is null            // root item only
                && row.PluginId.Contains("tmdb", StringComparison.OrdinalIgnoreCase))
            {
                var folderPath = TryGetFileScannerFolderPath(row.MediaItem!.MetadataJson);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    var nfoId = TryReadNfoTmdbId(folderPath);
                    if (!string.IsNullOrEmpty(nfoId))
                    {
                        // Determine prefix: TV shows get "tv:", movies get "movie:"
                        var mtName = NormalizeMediaTypeName(row.MediaItem.MediaType?.Name ?? string.Empty);
                        var prefix = mtName == "tv" ? "tv" : "movie";
                        row.ExternalId = $"{prefix}:{nfoId}";
                        logger.LogInformation(
                            "NFO sidecar match: item={ItemId} ({Name}) → {ExternalId}",
                            row.MediaItemId, row.MediaItem.Name, row.ExternalId);
                        try
                        {
                            var nfoResult = await ProviderCallGuard.CallAsync<MediaMetadata?>(
                                t => provider.GetByIdAsync(row.ExternalId, t), provider.PluginId, "GetByIdAsync", null, msg => logger.LogWarning("{Msg}", msg), msg => logger.LogError("{Msg}", msg), ct);

                            // Sanity-check against the item's ORIGINAL scanned title (derived from
                            // the file scanner's folder path), NOT row.MediaItem.Name. A local NFO
                            // is "authoritative" about which ID to use, but the file itself can be
                            // stale/wrong (leftover from a different tool, copy-paste mistake, a
                            // previous mis-scrape) -- confirmed directly: "Dave Matthews Band -
                            // Weekend On The Rocks (2005).avi" had a movie.nfo pointing at TMDB
                            // movie:72738, which is actually "VH1 Storytellers", a different DMB
                            // concert film. Trusting the ID unconditionally silently renamed the
                            // item to the wrong title with no way to tell from the log alone.
                            //
                            // Using Name instead of the folder path is a trap: once a bad match
                            // renames the item, Name IS the wrong title, so comparing a new
                            // candidate against it compares "wrong" against "wrong" and always
                            // passes -- this exact item survived a Refresh All for that reason
                            // before this fix. The on-disk folder name never gets corrupted, so
                            // it's the only reliable ground truth for what this item actually is.
                            var originalTitle = TryGetOriginalScannedTitle(row.MediaItem.MetadataJson)
                                ?? row.MediaItem.Name;
                            if (nfoResult is not null && !IsTitleMatchAcceptable(originalTitle, nfoResult.Title))
                            {
                                logger.LogWarning(
                                    "NFO sidecar match REJECTED: item={ItemId} \"{Name}\" (original scanned title " +
                                    "\"{OriginalTitle}\") -> {ExternalId} resolved to \"{MatchedTitle}\", which has " +
                                    "insufficient title overlap with the original scanned title -- the local NFO file " +
                                    "is almost certainly stale or wrong. Falling through to a normal name search " +
                                    "instead of trusting it. Check the folder's .nfo file directly if this keeps " +
                                    "happening for the same item.",
                                    row.MediaItemId, row.MediaItem.Name, originalTitle, row.ExternalId, nfoResult.Title);
                                row.ExternalId = null; // reset — fall through to name search
                            }
                            else
                            {
                                result = nfoResult;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning(
                                "NFO sidecar GetByIdAsync failed for {ExternalId}: {ErrorMessage}",
                                row.ExternalId, ex.Message);
                            row.ExternalId = null; // reset — fall through to name search
                        }
                    }
                }
            }

            if (result is null && row.ExternalId is null && row.MediaItem is not null)
            {
                var supportedTypes = provider.GetSupportedMediaTypes()
                    .Select(t => t.MediaTypeName)
                    .ToList();

                var mediaTypeName = await db.MediaTypes
                    .Where(t => t.Id == row.MediaItem.MediaTypeId)
                    .Select(t => t.Name)
                    .FirstOrDefaultAsync(ct);

                var typeIsSupported = mediaTypeName is not null &&
                    supportedTypes.Any(t => NormalizeMediaTypeName(t) == NormalizeMediaTypeName(mediaTypeName));

                // Unconditional, not Debug — this is the exact decision that determines whether
                // SearchAsync gets called for this (item, plugin) pair. Logged every time so
                // "why did plugin X get called for an item of type Y" never has to be guessed
                // at from ProviderCallGuard's timeout/error message alone, which carries no
                // item context. Kept at Information for supported items (expected, high-volume);
                // logged at Warning when unsupported since a match here means a Pending row
                // exists for a plugin that shouldn't have been queued for this item's type.
                //
                // DELIBERATELY PERMANENT — added 2026-08-02 to eliminate exactly this class of
                // guesswork after a live "why is Simkl being called for a music item" investigation
                // that static code reading alone couldn't resolve. Not a temporary debugging aid:
                // do not remove, downgrade to Debug, or cut for being "noisy" without the user's
                // explicit go-ahead. High per-item volume is the cost of never having to speculate
                // about provider dispatch again — that trade was made on purpose, keep it.
                if (typeIsSupported)
                    logger.LogInformation(
                        "EnrichPendingAsync: item {ItemId} \"{Name}\" (type={Type}) -> searching {Plugin} " +
                        "(declares: {SupportedTypes})",
                        row.MediaItemId, row.MediaItem.Name, mediaTypeName, provider.PluginId,
                        string.Join(", ", supportedTypes));
                else
                    logger.LogWarning(
                        "EnrichPendingAsync: item {ItemId} \"{Name}\" (type={Type}) has a Pending row for " +
                        "{Plugin}, which does NOT declare support for this type (declares: {SupportedTypes}) " +
                        "-- skipping search, but the row's mere existence means something upstream queued " +
                        "a mismatched (item, plugin) pair and should be investigated",
                        row.MediaItemId, row.MediaItem.Name, mediaTypeName ?? "(unresolved)", provider.PluginId,
                        string.Join(", ", supportedTypes));

                if (typeIsSupported)
                {
                    // For music items all hierarchy levels share the same media type name.
                    // We determine what MusicBrainz entity to search for from ParentId depth
                    // and build a Lucene-style query with parent/grandparent context so that
                    // album and track searches are precise rather than bare-name lookups.
                    //   no parent  → artist search: artist:"Metallica"
                    //   parent is root → album search: album:"Load" AND artist:"Metallica"
                    //   has grandparent → track search: track:"Until It Sleeps" AND artist:"Metallica" AND release:"Load"
                    searchQuery = row.MediaItem.Name;
                    if (mediaTypeName is "music" or "album" or "artist")
                    {
                        if (row.MediaItem.ParentId == null)
                        {
                            searchQuery = $"artist:{MbQuote(row.MediaItem.Name)}";
                        }
                        else
                        {
                            var parent = await db.MediaItems
                                .FirstOrDefaultAsync(m => m.Id == row.MediaItem.ParentId, ct);

                            if (parent?.ParentId == null)
                            {
                                // Album level — add artist context for precision.
                                // Strip leading "(YYYY) " from the album name before searching
                                // because file scanners prepend the year for sort order but
                                // MusicBrainz stores the canonical title without it.
                                var artistClause = !string.IsNullOrWhiteSpace(parent?.Name)
                                    ? $" AND artist:{MbQuote(parent.Name)}"
                                    : string.Empty;
                                searchQuery = $"album:{MbQuote(StripYearPrefix(row.MediaItem.Name))}{artistClause}";
                            }
                            else
                            {
                                // Track level — add artist and album context for precision.
                                // Strip "(YYYY) " prefix from the album (release) name.
                                var grandparent = await db.MediaItems
                                    .FirstOrDefaultAsync(m => m.Id == parent.ParentId, ct);
                                var artistClause = !string.IsNullOrWhiteSpace(grandparent?.Name)
                                    ? $" AND artist:{MbQuote(grandparent.Name)}"
                                    : string.Empty;
                                var releaseClause = !string.IsNullOrWhiteSpace(parent.Name)
                                    ? $" AND release:{MbQuote(StripYearPrefix(parent.Name))}"
                                    : string.Empty;
                                searchQuery = $"track:{MbQuote(row.MediaItem.Name)}{artistClause}{releaseClause}";
                            }
                        }
                    }

                    // For leaf-level items (tracks, episodes), include sibling names so
                    // plugins can use multi-track fingerprinting to pin down the exact release.
                    IReadOnlyList<string>? siblingNames = null;
                    IReadOnlyList<string>? childNames = null;
                    IReadOnlyList<SiblingInfo>? subItemMetadata = null;

                    if (row.MediaItem.HierarchyLevel == 2 && row.MediaItem.ParentId is not null)
                    {
                        // Leaf: fetch siblings for both SiblingNames and SubItemMetadata
                        var siblingItems = await db.MediaItems
                            .Where(m => m.ParentId == row.MediaItem.ParentId
                                     && m.Id       != row.MediaItem.Id)
                            .Take(50)
                            .ToListAsync(ct);
                        if (siblingItems.Count > 0)
                        {
                            siblingNames = siblingItems.Take(8).Select(m => m.Name).ToList().AsReadOnly();
                            subItemMetadata = siblingItems
                                .Select(s => AddDurationTier2(BuildSubItemMetadataTier1(s), s))
                                .ToList()
                                .AsReadOnly();
                        }
                    }
                    else if (row.MediaItem.HierarchyLevel <= 1)
                    {
                        // Parent: fetch children — shared between ChildNames and SubItemMetadata
                        var childItems = await db.MediaItems
                            .Where(m => m.ParentId == row.MediaItem.Id)
                            .Take(200)
                            .ToListAsync(ct);
                        if (childItems.Count > 0)
                        {
                            childNames = childItems.Select(m => m.Name).ToList().AsReadOnly();
                            subItemMetadata = childItems
                                .Select(c => AddDurationTier2(BuildSubItemMetadataTier1(c), c))
                                .ToList()
                                .AsReadOnly();
                        }
                    }

                    var filenameStem = ExtractFilenameStem(row.MediaItem);

                    // For flat media types like audiobooks the item has no hierarchy parent,
                    // so ParentName would be null. Pull the stored author from MetadataJson
                    // (written by the file scanner) so plugins can use it as an artist hint.
                    string? parentNameOverride    = null;
                    string? folderDerivedAltTitle = null;
                    if (row.MediaItem.HierarchyLevel == 0
                        && !string.IsNullOrWhiteSpace(row.MediaItem.MetadataJson))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(row.MediaItem.MetadataJson);
                            if (doc.RootElement.TryGetProperty("fileScanner", out var fs))
                            {
                                // Explicit author written by the scanner (preferred)
                                if (fs.TryGetProperty("author", out var authorEl))
                                    parentNameOverride = authorEl.GetString();

                                if (string.Equals(mediaTypeName, "audiobooks", StringComparison.OrdinalIgnoreCase)
                                    && fs.TryGetProperty("filePath", out var fpEl))
                                {
                                    var bookFolder = fpEl.GetString();
                                    if (!string.IsNullOrEmpty(bookFolder))
                                    {
                                        // Fallback author: derive from parent directory of book folder.
                                        // Items imported before the scanner wrote the author field explicitly
                                        // still have the folder path, so GetFileName(GetDirectoryName()) = author.
                                        if (parentNameOverride is null)
                                        {
                                            var parentDir = System.IO.Path.GetDirectoryName(bookFolder);
                                            if (!string.IsNullOrEmpty(parentDir))
                                                parentNameOverride = System.IO.Path.GetFileName(parentDir);
                                        }

                                        // Short folder-derived title: AudioAlbum tags often contain a
                                        // publisher subtitle ("Short Title: Series Name, Book N") that
                                        // MusicBrainz does not index.  The folder stores just the short
                                        // title after "(YYYY) - ", so prefer it as the primary search term.
                                        var folderName = System.IO.Path.GetFileName(bookFolder);
                                        var yearMatch = AudiobookFolderTitleRe.Match(folderName);
                                        if (yearMatch.Success)
                                            folderDerivedAltTitle = yearMatch.Groups[1].Value.Trim();
                                    }
                                }
                            }
                        }
                        catch { /* malformed JSON — leave null */ }
                    }

                    var fileScannedParent = parentNameOverride ?? row.MediaItem.Parent?.Name;
                    // For root items, pull any alternate names stored by a previous enrichment
                    // pass (e.g. Hardcover pen names) so we try all known name variants.
                    var storedAltNames = row.MediaItem.HierarchyLevel == 0
                        ? ExtractStoredAlternateNames(row.MediaItem.MetadataJson)
                        : null;

                    // Populate KnownExternalIds so artwork-only providers (e.g. Fanart.tv) can
                    // cross-reference TMDB / TVDB / MusicBrainz IDs without a text-search round-trip.
                    // Use GroupBy + First to safely handle rare duplicate-source rows that can arise
                    // after repeated merge/unmerge cycles (e.g. two "imdb" entries on one item).
                    var knownExternalIds = (await db.MediaExternalIds
                        .Where(e => e.MediaItemId == row.MediaItemId)
                        .ToListAsync(ct))
                        .GroupBy(e => e.Source.ToLowerInvariant())
                        .ToDictionary(g => g.Key, g => g.First().ExternalId);

                    // For child items (albums, seasons), also include parent external IDs prefixed
                    // with "parent_" so providers can cross-reference the parent's IDs.
                    // E.g. Fanart.tv needs the artist MBID (on the parent) to fetch album art.
                    if (row.MediaItem.ParentId is not null)
                    {
                        var parentIds = await db.MediaExternalIds
                            .Where(e => e.MediaItemId == row.MediaItem.ParentId)
                            .ToListAsync(ct);
                        foreach (var pid in parentIds)
                        {
                            var key = $"parent_{pid.Source.ToLowerInvariant()}";
                            knownExternalIds.TryAdd(key, pid.ExternalId);
                        }
                    }

                    var searchCtx = new MediaSearchContext(
                            Name:             row.MediaItem.Name,
                            Year:             ValidateYear(row.MediaItem.Year),
                            ParentName:       fileScannedParent,
                            GrandparentName:  row.MediaItem.Parent?.Parent?.Name,
                            ItemNumber:       row.MediaItem.Number,
                            HierarchyLevel:   row.MediaItem.HierarchyLevel,
                            FilenameStem:     filenameStem,
                            SiblingNames:     siblingNames,
                            AltTitles:        BuildAltTitles(
                                                  row.MediaItem.Name,
                                                  filenameStem,
                                                  folderDerivedAltTitle,
                                                  storedAltNames),
                            ChildNames:       childNames,
                            SubItemMetadata:  subItemMetadata,
                            MediaTypeName:    mediaTypeName,
                            KnownExternalIds: knownExternalIds.Count > 0 ? knownExternalIds : null);

                    logger.LogDebug(
                        "Searching {Plugin} for item {ItemId} \"{Name}\" " +
                        "(level={Level}, year={Year}, parent={Parent})",
                        provider.PluginId, row.MediaItemId, row.MediaItem.Name,
                        searchCtx.HierarchyLevel, searchCtx.Year, searchCtx.ParentName ?? "(none)");

                    var searchResults = await ProviderCallGuard.CallAsync(
                        t => provider.SearchAsync(searchCtx, t), provider.PluginId, "SearchAsync",
                        (IReadOnlyList<ScoredCandidate>)[], msg => logger.LogWarning("{Msg}", msg), msg => logger.LogError("{Msg}", msg), ct);

                    // Capture candidates for diagnostics BEFORE GetByIdAsync might overwrite result.
                    // Keep the whole ScoredCandidate (not just .Metadata) so the diagnostics view
                    // below can show the plugin's own real Score/ScoreReason -- the actual numbers
                    // the confidence gate acted on -- instead of an independently re-derived score
                    // that has no relationship to what actually happened.
                    rawCandidates = searchResults.Take(5).ToList();

                    var topCandidate = searchResults.OrderByDescending(c => c.Score).FirstOrDefault();
                    if (topCandidate is not null)
                        logger.LogDebug(
                            "Search returned {Count} candidates for item {ItemId} \"{Name}\" " +
                            "— top: \"{TopTitle}\" ({TopId}) score={Score} reasons={Reasons}",
                            searchResults.Count, row.MediaItemId, row.MediaItem.Name,
                            topCandidate.Metadata.Title, topCandidate.Metadata.ExternalId,
                            topCandidate.Score, topCandidate.ScoreReason);
                    else
                        logger.LogDebug(
                            "Search returned 0 candidates for item {ItemId} \"{Name}\" (plugin={Plugin})",
                            row.MediaItemId, row.MediaItem.Name, provider.PluginId);

                    // Confidence gate: reject low-scoring matches outright rather than accepting
                    // whatever scored highest — a near-zero-score candidate is not a real match.
                    if (topCandidate is not null && topCandidate.Score < DefaultConfidenceThreshold)
                    {
                        logger.LogInformation(
                            "Enrichment rejected for item {ItemId} '{ItemName}': top candidate '{Title}' " +
                            "scored {Score} (below threshold {Threshold}) — leaving as NotFound.",
                            row.MediaItemId, row.MediaItem.Name, topCandidate.Metadata.Title,
                            topCandidate.Score, DefaultConfidenceThreshold);
                        topCandidate = null;
                    }

                    // For file-scanner-created root items, require the matched title to cover at
                    // least 60% of the item's name tokens. Prevents fan-edit stubs whose names
                    // include a subtitle (e.g. "Alien - Darksteel Cut") from being silently
                    // identified as the canonical movie ("Alien") just because the first word
                    // matches. Sync-created items are exempt — their Name is already canonical.
                    if (topCandidate is not null
                        && row.MediaItem.HierarchyLevel == 0
                        && IsFileScannerItem(row.MediaItem)
                        && !IsTitleMatchAcceptable(row.MediaItem.Name, topCandidate.Metadata.Title))
                    {
                        logger.LogInformation(
                            "Enrichment skipped for item {ItemId} '{ItemName}': matched title '{MatchedTitle}' " +
                            "has insufficient token overlap with item name — leaving as NotFound. " +
                            "Use Fix Match to assign the correct identity manually.",
                            row.MediaItemId, row.MediaItem.Name, topCandidate.Metadata.Title);
                        topCandidate = null;
                    }

                    result = topCandidate?.Metadata;

                    // SearchAsync returns only search-index fields (no cover art).
                    // If we got a match, fetch the full entity so that PosterUrl
                    // and all other extended fields (genres, overview, etc.) are populated.
                    if (result is not null && !string.IsNullOrEmpty(result.ExternalId))
                    {
                        try
                        {
                            var fullResult = await ProviderCallGuard.CallAsync<MediaMetadata?>(
                                t => provider.GetByIdAsync(result.ExternalId, t), provider.PluginId, "GetByIdAsync", null, msg => logger.LogWarning("{Msg}", msg), msg => logger.LogError("{Msg}", msg), ct);
                            if (fullResult is not null && !string.IsNullOrEmpty(fullResult.ExternalId))
                                result = fullResult;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // Log without stack trace — GetByIdAsync failures are usually transient
                            // network issues (timeouts, provider outages). The search result is still
                            // usable for identification; we'll get full metadata on the next refresh.
                            logger.LogWarning(
                                "Follow-up GetByIdAsync failed for ExternalId={ExternalId} ({ErrorType}: {ErrorMessage}); keeping search result",
                                result.ExternalId, ex.GetType().Name, ex.Message);
                        }
                    }
                }
            }

            if (result is null || string.IsNullOrEmpty(result.ExternalId))
            {
                if (forceRefreshHadMatch && !string.IsNullOrEmpty(forceRefreshOriginalId))
                {
                    // Fresh re-derivation/re-search found nothing better — keep the
                    // previously-working match rather than downgrading it to NotFound.
                    logger.LogInformation(
                        "Force refresh found no new match for item {ItemId} (plugin={Plugin}); keeping existing match {ExternalId}",
                        row.MediaItemId, provider.PluginId, forceRefreshOriginalId);
                    row.ExternalId = forceRefreshOriginalId;
                }
                else
                {
                    logger.LogInformation(
                        "Enrichment not found: plugin={Plugin} item={ItemId} name={Name} query={Query} totalResults={Total}",
                        provider.PluginId, row.MediaItemId, row.MediaItem?.Name ?? "?",
                        searchQuery, result?.TotalResults ?? 0);
                    row.Status = EnrichmentStatus.NotFound;
                }
            }
            else
            {
                row.ExternalId      = result.ExternalId;
                row.Status          = EnrichmentStatus.Completed;
                row.LastCompletedAt = DateTime.UtcNow;
                row.ErrorMessage    = null;
                row.RetryCount      = 0;
                // Every plugin's raw result flows through here regardless of which provider
                // produced it, so this is the one place a bad URL (dead CDN domain, wrong
                // subdomain, stale/malformed link) can be caught before it's persisted and
                // shown to the user as if it were good data.
                await urlValidator.ValidateAndCleanAsync(result, ct);
                MergeMetadata(row.MediaItem!, row.PluginId, result);
                await resolutionService.ResolveAsync(row.MediaItem!, db, ct);
                // Keep media_external_ids in sync with the enrichment result so that
                // Fix Match (which calls this path with an IdOverride) actually persists
                // the new TMDB ID — not just the enrichment tracking row.
                await UpsertExternalIdForEnrichmentAsync(db, row.MediaItemId, result.ExternalId, ct, row.PluginId);
                // Cascade cross-ref IDs from this enrichment result to any other installed plugin
                // that declares it can accept that ID format. This ensures, for example, that a
                // successful TMDB enrichment seeds SIMKL and Trakt rows with the TMDB ID so they
                // can look up directly rather than falling back to a text search.
                await SeedCrossRefEnrichmentRowsAsync(db, row.MediaItemId, result, row.PluginId,
                    row.MediaItem!.MediaType?.Name, allProviders, ct);
                // If this is a TMDB movie enrichment, ensure collection parent exists and re-parent if needed.
                // Stubs are skipped — they're placeholders and must not trigger further collection creation.
                //
                // A collection intentionally created via the Add Collection page -- or already
                // linked to a real TMDB collection -- must never be treated as a plain movie for
                // re-parenting purposes, even while it still has zero members. Without this, a
                // brand-new empty collection whose OWN Fix Match/TMDB match happens to resolve to
                // a movie that itself belongs to some unrelated TMDB collection (exactly what
                // happened when "Metallica: S&M Collection" matched a single movie ID) would get
                // silently reparented UNDER that unrelated collection by EnsureCollectionParentAsync
                // below, destroying it as its own container. Mirrors the frontend's
                // isKnownCollection check (MediaDetailPage.tsx) on the backend, and is the same
                // container check merge eligibility and scraper candidate matching now share.
                var isKnownCollection = !row.MediaItem!.IsStub &&
                    await movieCollectionService.IsCollectionContainerAsync(db, row.MediaItemId, ct);

                // Load MediaType navigation if not already present (needed by EnsureCollectionParentAsync).
                if (!row.MediaItem!.IsStub && !isKnownCollection)
                {
                    if (row.MediaItem!.MediaType is null)
                        await db.Entry(row.MediaItem).Reference(m => m.MediaType).LoadAsync(ct);
                    await movieCollectionService.EnsureCollectionParentAsync(db, row.MediaItem!, row.PluginId, ct);
                }

                // After re-parenting under a collection, create stub entries for missing collection movies.
                // Reload the collection ExternalIds (set by EnsureCollectionParentAsync) so the stub
                // lookup can find the right collection:{id}.
                if (!row.MediaItem!.IsStub && row.MediaItem!.ParentId.HasValue)
                {
                    var collectionItem = await db.MediaItems
                        .Include(m => m.ExternalIds)
                        .Include(m => m.MediaType)
                        .FirstOrDefaultAsync(m => m.Id == row.MediaItem.ParentId!.Value, ct);
                    if (collectionItem is not null)
                        await movieCollectionService.EnsureCollectionStubsAsync(db, collectionItem, provider, ct, allProviders);
                }

                logger.LogInformation(
                    "Enrichment matched: plugin={Plugin} item={ItemId} \"{Name}\" (level={Level}) → {ExternalId} \"{MatchedTitle}\"",
                    provider.PluginId, row.MediaItemId, row.MediaItem?.Name ?? "?",
                    row.MediaItem?.HierarchyLevel ?? -1, result.ExternalId, result.Title);

                completedMeta = result;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (PluginAuthException ex)
        {
            // Authentication failure — terminal immediately, no retries.
            // The user must fix the plugin's credentials before enrichment can proceed.
            logger.LogWarning(
                "Plugin auth failure for item {ItemId} plugin {PluginId}: {ErrorMessage}",
                row.MediaItemId, row.PluginId, ex.Message);
            row.Status       = EnrichmentStatus.AuthFailed;
            row.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            // A 404 from the provider means "this item definitively does not exist upstream" —
            // treat as NotFound rather than a transient error so retries are not wasted.
            // Example: TMDB seasons/episodes that are not yet in TMDB's database return 404.
            if (ex is ProviderNotFoundException ||
                (ex is HttpRequestException httpEx &&
                 httpEx.StatusCode == System.Net.HttpStatusCode.NotFound))
            {
                logger.LogInformation(
                    "Enrichment not found: plugin={Plugin} item={ItemId} \"{Name}\" — {Reason}",
                    row.PluginId, row.MediaItemId, row.MediaItem?.Name ?? "?",
                    ex is ProviderNotFoundException ? "provider returned no match for stored ID" : "provider returned 404");
                row.Status       = EnrichmentStatus.NotFound;
                row.ErrorMessage = ex.Message;
            }
            else if (ex is InvalidOperationException)
            {
                // Plugin threw "not configured" or similar setup failure — retrying won't help.
                // Mark as Skipped so the batch doesn't keep retrying a misconfigured plugin.
                logger.LogInformation(
                    "Enrichment skipped for item {ItemId} plugin {PluginId}: {ErrorMessage}",
                    row.MediaItemId, row.PluginId, ex.Message);
                row.Status       = EnrichmentStatus.Skipped;
                row.ErrorMessage = ex.Message;
            }
            else
            {
                // Include stack trace only for unexpected errors; HTTP/timeout errors are self-describing.
                // Note: HttpClient timeout throws TaskCanceledException with its own internal token —
                // that is NOT an external cancellation and must be caught here so the batch continues.
                var isExpected = ex is HttpRequestException or TaskCanceledException or TimeoutException
                                                            or OperationCanceledException;
                if (isExpected)
                    logger.LogWarning(
                        "Enrichment failed for item {ItemId} plugin {PluginId}: {ErrorType}: {ErrorMessage}",
                        row.MediaItemId, row.PluginId, ex.GetType().Name, ex.Message);
                else
                    logger.LogWarning(ex, "Enrichment failed for item {ItemId} plugin {PluginId}",
                        row.MediaItemId, row.PluginId);
                row.RetryCount++;
                row.ErrorMessage = ex.Message;
                row.Status = row.RetryCount >= row.MaxRetries
                    ? EnrichmentStatus.Exhausted
                    : EnrichmentStatus.Failed;
            }
        }

        // ── Capture diagnostics ────────────────────────────────────────────────
        try
        {
            var candidates = rawCandidates
                .Select(c => new EnrichCandidate(
                    c.Metadata.Title, c.Metadata.Year, c.Metadata.ExternalId, c.Score, c.ScoreReason))
                .OrderByDescending(c => c.TotalScore)
                .ToList();

            var failureReason = row.Status switch
            {
                // "No results" is only true when the provider genuinely returned nothing --
                // rawCandidates.Count > 0 means candidates DID come back but were rejected by
                // the confidence gate or title-overlap check further up. Reporting both cases
                // with the same "no results" text was actively misleading: the candidate list
                // shown right below this message could contain the correct match.
                EnrichmentStatus.NotFound when rawCandidates.Count > 0 =>
                    $"{rawCandidates.Count} candidate(s) were returned but none met the confidence " +
                    "threshold to be auto-selected. Use Fix Match to pick one manually if it's correct.",
                EnrichmentStatus.NotFound  => "No results returned by the provider for this search query.",
                EnrichmentStatus.Failed    => row.ErrorMessage ?? "Provider call threw an exception.",
                EnrichmentStatus.Exhausted => "Maximum retries reached with no successful match.",
                EnrichmentStatus.Completed => "Matched successfully.",
                _ => string.Empty
            };

            var diag = new EnrichDiagnostics(
                searchQuery,
                rawCandidates.Count,
                DefaultConfidenceThreshold,
                failureReason,
                candidates,
                ReadScannerSignals(row.MediaItem));

            row.DiagnosticsJson = JsonSerializer.Serialize(diag,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (Exception diagEx)
        {
            logger.LogWarning(diagEx,
                "Failed to build enrichment diagnostics for item {ItemId}", row.MediaItemId);
        }

        await db.SaveChangesAsync(ct);

        // Single-item callers (Refresh, Fix Match, Resync All) default Cascade to true.
        // The batch pass (EnrichPendingAsync) passes Cascade: false — it already walks the
        // full hierarchy itself via its own parent-then-child ordered loop, so recursing here
        // too would process every child twice.
        if (options.Cascade)
            await CascadeToChildrenAsync(db, provider, pluginId, row.MediaItem!, options, ct, allProviders);

        return completedMeta;
    }

    /// <summary>
    /// Thrown only by the inner GetByIdAsync catch to signal "provider returned no match for
    /// this specific ID". Caught exclusively by the outer EnrichItemCoreLockedAsync handler so
    /// that unrelated KeyNotFoundExceptions from dictionary access or LINQ elsewhere in the
    /// method do NOT get silently marked as NotFound.
    /// </summary>
    private sealed class ProviderNotFoundException : Exception
    {
        public ProviderNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    private static EnrichScannerSignals? ReadScannerSignals(MediaItem? item)
    {
        if (item?.MetadataJson is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(item.MetadataJson);
            if (!doc.RootElement.TryGetProperty("fileScanner", out var fs)) return null;
            string? folder = fs.TryGetProperty("folderPath", out var fp) ? fp.GetString() : null;
            bool hasNfo    = fs.TryGetProperty("nfoPosterUrl", out var npo)
                             && npo.ValueKind == JsonValueKind.String
                             && !string.IsNullOrEmpty(npo.GetString());
            bool hasPoster = fs.TryGetProperty("localPosterPath", out var lp)
                             && lp.ValueKind == JsonValueKind.String
                             && !string.IsNullOrEmpty(lp.GetString());
            return new EnrichScannerSignals(folder, hasNfo, hasPoster, null);
        }
        catch { return null; }
    }

    /// <summary>
    /// Normalises a DB media-type name to the canonical form used by plugin
    /// <c>GetSupportedMediaTypes()</c> declarations.  The DB stores "movies" (plural)
    /// while many standalone plugins (e.g. TMDB) declare "movie" (singular).
    /// Both map to the same concept; treat them as equivalent for matching purposes.
    /// </summary>
    private static string NormalizeMediaTypeName(string name) =>
        name.Equals("movies", StringComparison.OrdinalIgnoreCase) ? "movie" : name.ToLowerInvariant();

    /// <summary>
    /// Returns all raw DB name variants that <see cref="NormalizeMediaTypeName"/> would map to
    /// the same canonical form as <paramref name="name"/>. Used to build a SQL-translatable set
    /// for <see cref="EnrichPendingAsync"/> so the media-type filter runs in SQL rather than
    /// in memory after the rows are fetched.
    /// </summary>
    private static IEnumerable<string> ExpandMediaTypeName(string name)
    {
        var lower = name.ToLowerInvariant();
        // "movie" and "movies" normalize to the same canonical form — include both variants.
        // Always return lowercase so EF-generated SQL IN ('movie','movies') matches the DB's
        // lowercase values. StringComparer.OrdinalIgnoreCase on the HashSet only helps C#
        // lookups; SQL IN literals are compared case-sensitively in SQLite.
        if (lower == "movie" || lower == "movies")
            return ["movie", "movies"];
        return [lower];
    }

    /// <summary>
    /// Marks Pending rows for <paramref name="pluginId"/> as Skipped when their MediaItem's
    /// type isn't in <paramref name="supportedRawTypes"/>. These rows are permanently excluded
    /// by <see cref="EnrichPendingAsync"/>'s own per-pass WHERE clause and can never become
    /// eligible, so leaving them Pending means they'd sit there forever as noise (or worse,
    /// keep the batch loop's "Pending rows remain, do another pass" guard spinning forever —
    /// see the call site before that loop for why this must run first, not just after).
    /// </summary>
    private async Task MarkUnsupportedPendingAsSkippedAsync(
        ChronicleDbContext db, string pluginId, HashSet<string> supportedRawTypes, CancellationToken ct)
    {
        if (supportedRawTypes.Count == 0) return;

        var unsupportedPending = await db.MediaEnrichments
            .Include(x => x.MediaItem).ThenInclude(m => m!.MediaType)
            .Where(x => x.PluginId == pluginId &&
                        x.Status == EnrichmentStatus.Pending &&
                        (x.MediaItem!.MediaType == null ||
                         !supportedRawTypes.Contains(x.MediaItem!.MediaType!.Name)))
            .ToListAsync(ct);

        if (unsupportedPending.Count == 0) return;

        foreach (var row in unsupportedPending)
        {
            row.Status       = EnrichmentStatus.Skipped;
            row.ErrorMessage = $"Media type '{row.MediaItem?.MediaType?.Name ?? "unknown"}' " +
                               $"is not supported by plugin '{pluginId}'.";
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "EnrichPendingAsync: marked {Count} items as Skipped for {PluginId} — " +
            "media type not supported by this plugin",
            unsupportedPending.Count, pluginId);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Strips Lucene range/special operators then wraps in double quotes for exact phrase matching.
    /// Operators like &lt;&gt;{}[]^~ break MusicBrainz SOLR if left unescaped in the query string.
    /// </summary>
    private static string MbQuote(string term)
    {
        // Remove Lucene range and boost operators that cannot be safely escaped inside phrases
        term = System.Text.RegularExpressions.Regex.Replace(term, @"[<>{}[\]^~]", "").Trim();
        return $"\"{term.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    /// <summary>
    /// Strips a leading "(YYYY) " year prefix from a name before building MusicBrainz queries.
    /// File scanners often prepend the year (e.g. "(2008) 3 Doors Down") for sort order, but
    /// MusicBrainz stores the canonical title without it.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex YearPrefixRe =
        new(@"^\(\d{4}\)\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripYearPrefix(string name) =>
        YearPrefixRe.Replace(name, string.Empty);

    private static readonly System.Text.RegularExpressions.Regex YearSuffixRe =
        new(@"\s*\(\d{4}\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripYearSuffix(string name) =>
        YearSuffixRe.Replace(name, string.Empty);

    /// <summary>
    /// Returns the year if it falls within the plausible media release range
    /// (1900 to current year + 3). Returns null for values outside that range
    /// so plugins do not waste a search attempt on a garbage year.
    /// </summary>
    internal static int? ValidateYear(int? year)
    {
        if (year is null) return null;
        var maxYear = DateTime.UtcNow.Year + 3;
        return year >= 1900 && year <= maxYear ? year : null;
    }

    private static readonly System.Text.RegularExpressions.Regex VersionQualifierEnrichRe =
        new(@"\s*\([^)]+\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Builds an ordered, deduplicated list of title forms to try in each search stage.
    /// Order: PreciseName (if any) → year-stripped canonical name → filenameStem (if different) →
    /// version-qualifier-stripped form → any stored alternate names (pen names, name variants).
    /// </summary>
    internal static IReadOnlyList<string> BuildAltTitles(
        string name, string? filenameStem, string? preciseName,
        IEnumerable<string>? alternateNames = null)
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();

        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            var trimmed = s.Trim();
            if (seen.Add(trimmed)) results.Add(trimmed);
        }

        // 1. Precise name (NFO/reliable source) first
        Add(preciseName);

        // 2. Year-stripped canonical name (strip both prefix and suffix patterns)
        var stripped = StripYearPrefix(StripYearSuffix(name)).Trim();
        Add(string.IsNullOrWhiteSpace(stripped) ? name : stripped);

        // 3. Filename stem (often cleaner than the tag title)
        Add(filenameStem);

        // 4. Version-qualifier-stripped form (e.g. "Kryptonite" from "Kryptonite (LP version)")
        //    Apply to the year-stripped form (results[0] after preciseName, or results[0] overall)
        var baseForStripping = results.Count > 0
            ? results[preciseName != null ? Math.Min(1, results.Count - 1) : 0]
            : name;
        var noQualifier = VersionQualifierEnrichRe.Replace(baseForStripping, string.Empty).Trim();
        Add(noQualifier);

        // 5. Stored alternate names (pen names, name variants from previous enrichment)
        if (alternateNames is not null)
            foreach (var n in alternateNames)
                Add(n);

        return results.AsReadOnly();
    }

    /// <summary>
    /// Reads all <c>alternateNames</c> arrays stored in any plugin's section of
    /// <c>metadata_json</c>.  Used to surface pen names and name variants as additional
    /// search terms when re-enriching author items.
    /// </summary>
    private static IReadOnlyList<string>? ExtractStoredAlternateNames(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc   = System.Text.Json.JsonDocument.Parse(metadataJson);
            var       names = new List<string>();
            foreach (var plugin in doc.RootElement.EnumerateObject())
            {
                if (plugin.Value.TryGetProperty("alternateNames", out var altEl)
                    && altEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var el in altEl.EnumerateArray())
                    {
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            names.Add(s.Trim());
                    }
                }
            }
            return names.Count > 0 ? names.AsReadOnly() : null;
        }
        catch { return null; }
    }

    // Leading track-number prefix: "01 - ", "02. ", "3 ", "1-01 - " etc.
    private static readonly System.Text.RegularExpressions.Regex TrackNumPrefixRe =
        new(@"^\d{1,2}(?:-\d{1,2})?[\s\-._]+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Derives a clean search-friendly title from the first file path stored in the item's
    /// fileScanner metadata.  Returns null when no file path is available or when the
    /// resulting stem is not meaningfully different from <paramref name="item"/>.Name.
    /// </summary>
    private static string? ExtractFilenameStem(Chronicle.Core.Models.MediaItem item)
    {
        if (string.IsNullOrEmpty(item.MetadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(item.MetadataJson);
            if (!doc.RootElement.TryGetProperty("fileScanner", out var fs)) return null;

            string? filePath = null;
            if (fs.TryGetProperty("filePaths", out var fps) &&
                fps.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                filePath = fps.EnumerateArray().FirstOrDefault().GetString();
            }
            if (filePath is null && fs.TryGetProperty("filePath", out var fp))
                filePath = fp.GetString();
            if (filePath is null) return null;

            var stem = System.IO.Path.GetFileNameWithoutExtension(filePath);
            // Strip leading track numbers: "01 - Duck and Run" → "Duck and Run"
            stem = TrackNumPrefixRe.Replace(stem, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(stem)) return null;

            // Only return when meaningfully different from the item name (case-insensitive).
            return string.Equals(stem, item.Name, StringComparison.OrdinalIgnoreCase)
                ? null : stem;
        }
        catch { return null; }
    }

    // Matches plain track prefix ("01 - Song") or disc-track prefix ("1-01 - Song").
    // Captures the last numeric group before the separator so disc-track gives the
    // track number (01), not the disc number (1).
    // Distinct from TrackNumPrefixRe which has no capture group and is used by ExtractFilenameStem.
    private static readonly System.Text.RegularExpressions.Regex TrackPrefixRe =
        new(@"^(?:\d{1,2}-)?(\d{1,3})[\s\-\.]+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Disc/CD folder pattern — e.g. "Disc 1", "disk2", "CD 3"
    private static readonly System.Text.RegularExpressions.Regex DiscFolderRe =
        new(@"\b(?:disc|disk|cd)\s*(\d+)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Audiobook folder title: captures the title segment that follows "(YYYY) - ".
    // e.g. "Dungeon Crawler Carl - 2 - (2021) - Carl's Doomsday Scenario" → "Carl's Doomsday Scenario"
    // Used to prefer the short folder title over an AudioAlbum tag with a publisher subtitle.
    private static readonly System.Text.RegularExpressions.Regex AudiobookFolderTitleRe =
        new(@"\(\d{4}\)\s*-\s*(.+)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Tier 1: extract what we can from filename and folder path alone — no file I/O beyond
    /// what the file scanner has already stored in MetadataJson.
    /// </summary>
    internal static SiblingInfo BuildSubItemMetadataTier1(Chronicle.Core.Models.MediaItem item)
    {
        string? filePath   = null;
        string? folderPath = null;

        if (!string.IsNullOrEmpty(item.MetadataJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(item.MetadataJson);
                if (doc.RootElement.TryGetProperty("fileScanner", out var fs))
                {
                    if (fs.TryGetProperty("filePaths", out var fps) && fps.GetArrayLength() > 0)
                        filePath = fps[0].GetString();
                    if (fs.TryGetProperty("folderPath", out var fp))
                        folderPath = fp.GetString();
                }
            }
            catch { /* corrupt JSON — ignore */ }
        }

        int? trackNumber = null;
        int? discNumber  = null;

        if (filePath is not null)
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            var tm = TrackPrefixRe.Match(fileName);
            if (tm.Success && int.TryParse(tm.Groups[1].Value, out var tn))
                trackNumber = tn;
        }

        if (folderPath is not null)
        {
            var dm = DiscFolderRe.Match(folderPath);
            if (dm.Success && int.TryParse(dm.Groups[1].Value, out var dn))
                discNumber = dn;
        }

        return new SiblingInfo(
            Name:       item.Name,
            ItemNumber: trackNumber,
            DiscNumber: discNumber);
    }

    /// <summary>
    /// Tier 2: add duration (in seconds) from fileScanner metadata already stored in
    /// MetadataJson. No additional file I/O — the duration was captured during the scan.
    /// </summary>
    internal static SiblingInfo AddDurationTier2(SiblingInfo tier1, Chronicle.Core.Models.MediaItem item)
    {
        if (string.IsNullOrEmpty(item.MetadataJson)) return tier1;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(item.MetadataJson);
            if (doc.RootElement.TryGetProperty("fileScanner", out var fs) &&
                fs.TryGetProperty("duration", out var dur) &&
                dur.TryGetInt32(out var seconds))
            {
                return tier1 with { DurationSeconds = seconds };
            }
        }
        catch { }
        return tier1;
    }

    // ── NFO sidecar helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Extracts the folder path stored by the file scanner in a media item's MetadataJson,
    /// so that enrichment can look for NFO sidecars without re-walking the file system.
    /// </summary>
    private static string? TryGetFileScannerFolderPath(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("fileScanner", out var fs) &&
                fs.TryGetProperty("folderPath", out var fp))
                return fp.GetString();
        }
        catch { /* malformed JSON */ }
        return null;
    }

    /// <summary>
    /// Derives the item's ORIGINAL scanned title from the file scanner's stored folder path,
    /// rather than the item's current (possibly already-corrupted) Name. A prior bad match can
    /// permanently overwrite MediaItem.Name -- e.g. "Dave Matthews Band - Weekend On The Rocks
    /// (2005)" got renamed to "Dave Matthews Band - VH1 Storytellers" by an earlier wrong match.
    /// Comparing a NEW candidate against the already-wrong Name is comparing "wrong" against
    /// "wrong", which passes as a match and can never self-heal. The folder name on disk doesn't
    /// change when a match goes wrong, so it's the only reliable ground truth left. Falls back to
    /// the current Name (old behaviour) if the folder path isn't available.
    /// </summary>
    private static string? TryGetOriginalScannedTitle(string? metadataJson)
    {
        var folderPath = TryGetFileScannerFolderPath(metadataJson);
        if (string.IsNullOrEmpty(folderPath)) return null;
        var leaf = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
    }

    /// <summary>
    /// Looks for tvshow.nfo / movie.nfo (and any *.nfo as fallback) in
    /// <paramref name="folderPath"/> and returns the numeric TMDB ID from
    /// &lt;uniqueid type="tmdb"&gt; if present.
    /// </summary>
    private static string? TryReadNfoTmdbId(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return null;
        try
        {
            // Prefer well-known names (Kodi/Jellyfin convention)
            foreach (var name in new[] { "tvshow.nfo", "movie.nfo" })
            {
                var path = Path.Combine(folderPath, name);
                if (File.Exists(path))
                {
                    var id = _nfoExtractor.Extract(path)?.ExternalId;
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            // Fallback: first *.nfo found in the folder (excluding sub-folders)
            var any = Directory.EnumerateFiles(folderPath, "*.nfo").FirstOrDefault();
            if (any is not null)
            {
                var id = _nfoExtractor.Extract(any)?.ExternalId;
                if (!string.IsNullOrEmpty(id)) return id;
            }
        }
        catch { /* I/O error — network drive unavailable etc. */ }
        return null;
    }

    // ── Diagnostics DTOs (serialised to DiagnosticsJson) ─────────────────────

    private sealed record EnrichDiagnostics(
        string SearchQuery,
        int CandidatesReturned,
        int Threshold,
        string FailureReason,
        List<EnrichCandidate> TopCandidates,
        EnrichScannerSignals? ScannerSignals);

    private sealed record EnrichCandidate(
        string? Title,
        int? Year,
        string? ExternalId,
        int TotalScore,
        string? ScoreReason);

    private sealed record EnrichScannerSignals(
        string? FolderPath,
        bool HasNfo,
        bool HasLocalPoster,
        double? ConfidenceScore);

    private static void MergeMetadata(MediaItem item, string pluginId, MediaMetadata result)
    {
        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            item.MetadataJson ?? "{}") ?? [];

        // Remove any short-ID alias key (e.g. "tmdb") that old code may have written.
        var shortId = pluginId.Contains('.') ? PluginIdHelper.ToSource(pluginId) : null;
        if (shortId is not null) existing.Remove(shortId);

        // Clear the Results/TotalResults fields before serializing — they are search-result
        // list data used by the UI and must not be persisted (they also create a circular
        // reference when the best result points back into its own Results list).
        var savedResults = result.Results;
        var savedTotal   = result.TotalResults;
        result.Results      = null;
        result.TotalResults = 0;
        try
        {
            existing[pluginId] = JsonSerializer.SerializeToElement(result, MetadataBlobOptions);
            item.MetadataJson  = JsonSerializer.Serialize(existing);
        }
        finally
        {
            result.Results      = savedResults;
            result.TotalResults = savedTotal;
        }

        // NOTE: PosterUrl is NOT set directly here — ResolveAsync() is called immediately
        // after MergeMetadata and promotes it according to the priority assignment config.
        // The one exception: child items (level > 0) that previously had a TMDB-hosted poster
        // from a wrong match must have it cleared when the new enrichment returns no poster,
        // otherwise the stale image persists. ResolveAsync won't clear it because the blob
        // will simply have no posterUrl.
        if (string.IsNullOrEmpty(result.PosterUrl)
            && item.HierarchyLevel > 0
            && item.PosterUrl?.StartsWith("https://image.tmdb.org/", StringComparison.OrdinalIgnoreCase) == true)
        {
            item.PosterUrl = null;
        }

        item.UpdatedAt = DateTime.UtcNow;
    }

    // ── EnrichItemCoreAsync (item overload — thin wrapper) ─────────────────────
    //
    // Loads or creates the row for (item, pluginId), then delegates to the canonical
    // row-based EnrichItemCoreAsync above, which every caller (batch, single-item,
    // cascade) ultimately funnels through. PluginAuthException is deliberately NOT
    // rethrown by the row-based overload (a batch pass must keep processing the rest
    // of its items after one auth failure) — this wrapper re-derives it from the row's
    // final status instead, so the single-item callers below still get to propagate
    // PLUGIN_AUTH_FAILED to their controller the same way they did before.
    private async Task<MediaMetadata?> EnrichItemCoreAsync(
        ChronicleDbContext db,
        IMetadataProvider provider,
        string pluginId,
        MediaItem item,
        EnrichmentOptions options,
        CancellationToken ct,
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)>? allProviders = null)
    {
        var row = await db.MediaEnrichments
            .FirstOrDefaultAsync(e => e.MediaItemId == item.Id && e.PluginId == pluginId, ct);
        if (row is null)
        {
            row = new MediaItemEnrichment
                { MediaItemId = item.Id, PluginId = pluginId, MaxRetries = 3 };
            db.MediaEnrichments.Add(row);
        }
        row.MediaItem = item;

        var completedMeta = await EnrichItemCoreAsync(db, provider, pluginId, row, options, ct, allProviders);

        if (row.Status == EnrichmentStatus.AuthFailed)
            throw new PluginAuthException(pluginId, row.ErrorMessage ?? "Plugin authentication failed.");

        return completedMeta;
    }

    private async Task CascadeToChildrenAsync(
        ChronicleDbContext db, IMetadataProvider provider, string pluginId,
        MediaItem parent, EnrichmentOptions options, CancellationToken ct,
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)>? allProviders = null)
    {
        var children = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.Parent)
            .Where(m => m.ParentId == parent.Id)
            .OrderBy(m => m.Number).ThenBy(m => m.Name)
            .ToListAsync(ct);

        foreach (var child in children)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                // Cross-ref seeding and SaveChangesAsync already happen inside
                // EnrichItemCoreAsync itself — nothing further needed here.
                await EnrichItemCoreAsync(db, provider, pluginId, child,
                    options with { IdOverride = null }, ct, allProviders);
            }
            catch (OperationCanceledException) { return; }
            catch (PluginAuthException ex)
            {
                logger.LogWarning(ex, "Cascade: auth failure enriching child {Id} '{Name}'",
                    child.Id, child.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cascade: failed enriching child {Id} '{Name}'",
                    child.Id, child.Name);
            }
        }
    }

    private static string NormalizeSearchName(string name) =>
        System.Text.RegularExpressions.Regex
            .Replace(name, @"[:\-,]", " ")   // keep apostrophes and dots — dots are meaningful in names like "shutdown.exe"
            .Replace("  ", " ")
            .Trim()
            .ToLowerInvariant();

    /// <summary>
    /// Returns true if the item's MetadataJson indicates it was created by the file scanner
    /// (as opposed to a sync-created or manually-added stub).
    /// </summary>
    private static bool IsFileScannerItem(MediaItem item) =>
        item.MetadataJson is not null && item.MetadataJson.Contains("\"fileScanner\"");

    // Compiled regex for trailing year used by IsTitleMatchAcceptable.
    private static readonly System.Text.RegularExpressions.Regex _trailingYearRe =
        new(@"\s*[\(\[]\d{4}[\)\]]\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Returns true if <paramref name="matchedTitle"/> covers enough of <paramref name="itemName"/>
    /// that the match is trustworthy. Uses token-level Jaccard similarity (≥ 0.60) after stripping
    /// trailing year suffixes and normalising punctuation.
    ///
    /// This catches fan-edit stubs like "Alien - Darksteel Cut (2023)" being incorrectly identified
    /// as "Alien" (1979): the item has three meaningful tokens [alien, darksteel, cut] while the
    /// matched title has only one [alien], giving Jaccard = 1/3 ≈ 0.33 → rejected.
    /// </summary>
    private static bool IsTitleMatchAcceptable(string itemName, string? matchedTitle)
    {
        if (string.IsNullOrWhiteSpace(matchedTitle)) return false;

        // Strip trailing year/bracket: "Alien - Darksteel Cut (2023)" → "Alien - Darksteel Cut"
        var strippedItem  = _trailingYearRe.Replace(itemName, "").Trim();
        var strippedTitle = _trailingYearRe.Replace(matchedTitle, "").Trim();

        // Normalise: lower, replace common separators with space, collapse whitespace.
        static string Norm(string s) =>
            System.Text.RegularExpressions.Regex.Replace(
                s.ToLowerInvariant()
                 .Replace(":", " ")
                 .Replace("-", " ")
                 .Replace(",", " ")
                 .Replace("'", "")
                 .Replace("\"", ""),
                @"\s+", " ").Trim();

        var normItem  = Norm(strippedItem);
        var normTitle = Norm(strippedTitle);

        if (normItem == normTitle) return true;   // exact match after normalisation

        // Token Jaccard similarity
        var itemTokens  = normItem .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var titleTokens = normTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

        var intersection = itemTokens.Count(t => titleTokens.Contains(t));
        var union        = itemTokens.Count + titleTokens.Count - intersection;

        if (union == 0) return true;
        return (double)intersection / union >= 0.60;
    }

    /// <summary>
    /// Parses a Chronicle external-ID string into (source, externalId).
    /// "movie:550"      → ("tmdb", "movie:550")
    /// "tv:1396"        → ("tmdb", "tv:1396")
    /// "imdb:tt0137523" → ("imdb", "tt0137523")
    /// </summary>
    private static (string Source, string ExternalId) ParseExternalId(string rawId)
    {
        if (rawId.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
            return ("imdb", rawId[5..]);
        return ("tmdb", rawId);
    }

    /// <summary>
    /// Seeds enrichment rows for any installed plugin that declares it accepts a cross-ref
    /// ID present in <paramref name="result"/>.ExtendedData. Called after every successful
    /// enrichment so that, e.g., a TMDB result automatically seeds SIMKL and Trakt rows
    /// with the known TMDB ID — allowing those plugins to look up directly rather than
    /// falling back to a text search.
    ///
    /// Only inserts rows where none already exist (Pending/Completed/etc.) — never resets
    /// an already-started row.
    /// </summary>
    private async Task SeedCrossRefEnrichmentRowsAsync(
        ChronicleDbContext db,
        int mediaItemId,
        Chronicle.Plugins.Models.MediaMetadata result,
        string sourcePluginId,
        string? mediaTypeName,
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)>? allProviders,
        CancellationToken ct)
    {
        if (allProviders is null || allProviders.Count == 0) return;

        var fromSource = PluginIdHelper.ToSource(sourcePluginId);
        var crossRefs  = CrossRefHelper.ExtractCrossRefIds(result, fromSource, mediaTypeName);
        if (crossRefs.Count == 0) return;

        // Only seed plugins that actually support this item's media type (or its parent hint).
        var normalizedType = mediaTypeName is not null ? NormalizeMediaTypeName(mediaTypeName) : null;
        // Parent-type hint: anime → tv, fanedits → movie (mirrors FileScanService.ToMediaTypeHint).
        // anime_movies is checked first — it contains "anime" as a substring but is flat (like
        // movies), not TV-hierarchical, so it must not fall through to the anime → tv case.
        var typeHint = mediaTypeName?.ToLowerInvariant() switch
        {
            var n when n is not null && n.Contains("anime") && n.Contains("movie") => "movie",
            var n when n is not null && n.Contains("anime")    => "tv",
            var n when n is not null && n.Contains("fanedits") => "movie",
            _ => null,
        };

        // Track which plugins have already been seeded in this call to prevent duplicate EF
        // Add() calls when multiple cross-ref entries (e.g. tmdb: and imdb:) both match the
        // same candidate plugin — the AnyAsync check would pass for both before either saves.
        var seededThisCall = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (xSource, xId) in crossRefs)
        {
            foreach (var (candidatePluginId, candidateProvider) in allProviders)
            {
                if (candidatePluginId == sourcePluginId) continue;
                if (seededThisCall.Contains(candidatePluginId)) continue;

                // Skip plugins that don't support this media type.
                if (normalizedType is not null)
                {
                    var supported = candidateProvider.GetSupportedMediaTypes()
                        .Any(t =>
                        {
                            var n = NormalizeMediaTypeName(t.MediaTypeName);
                            return n == normalizedType || n == typeHint;
                        });
                    if (!supported) continue;
                }

                var isOwner        = string.Equals(PluginIdHelper.ToSource(candidatePluginId), xSource, StringComparison.OrdinalIgnoreCase);
                var acceptsCrossRef = !isOwner && candidateProvider
                    .GetAcceptedCrossRefPrefixes()
                    .Any(prefix => xId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                if (!isOwner && !acceptsCrossRef) continue;

                var alreadyExists = await db.MediaEnrichments
                    .AnyAsync(r => r.MediaItemId == mediaItemId && r.PluginId == candidatePluginId, ct);
                if (alreadyExists) continue;

                db.MediaEnrichments.Add(new Chronicle.Core.Models.MediaItemEnrichment
                {
                    MediaItemId = mediaItemId,
                    PluginId    = candidatePluginId,
                    ExternalId  = xId,
                    Status      = Chronicle.Core.Models.EnrichmentStatus.Pending,
                });
                // Also write the external ID row so KnownExternalIds and stage-2 sync matching
                // see the seeded ID immediately, before the enrichment row is processed.
                await UpsertExternalIdForEnrichmentAsync(db, mediaItemId, xId, ct, candidatePluginId);
                seededThisCall.Add(candidatePluginId);

                logger.LogInformation(
                    "CrossRef seed: plugin={Plugin} item={ItemId} ← {Source}={ExternalId} (from {SourcePlugin})",
                    candidatePluginId, mediaItemId, xSource, xId, sourcePluginId);
            }
        }
    }

    /// <summary>
    /// Upserts a <see cref="MediaExternalId"/> row so the <c>media_external_ids</c> table
    /// always reflects the current enrichment match.  Unlike the insert-if-missing helper
    /// in FileScanService, this replaces an existing row for the same source because Fix
    /// Match can legitimately change an item from one TMDB entry to another.
    /// </summary>
    private async Task UpsertExternalIdForEnrichmentAsync(
        ChronicleDbContext db, int mediaItemId, string rawExternalId, CancellationToken ct,
        string? excludePluginId = null)
    {
        // Derive the source from the calling plugin's short ID so each plugin writes to
        // its own row in media_external_ids. The old ParseExternalId fallback mapped every
        // non-IMDB format to "tmdb", which caused MusicBrainz, Hardcover, SIMKL, etc. to
        // all share one "tmdb" row per item. When plugin B later processed the same item
        // and stored a different ExternalId, it would find plugin A's value in that shared
        // row, detect a change (idChanged = true), and cascade-reset all sibling enrichment
        // rows — including plugin A's Completed row — back to Pending spuriously.
        string source;
        string extId;

        if (rawExternalId.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
        {
            // IMDB IDs from any plugin are stored in a shared "imdb" source row.
            source = "imdb";
            extId  = rawExternalId[5..];
        }
        else if (excludePluginId is not null)
        {
            // Normal path: source = last segment of the calling plugin's full ID.
            //   "chronicle.plugin.tmdb"        → "tmdb"
            //   "chronicle.plugin.musicbrainz" → "musicbrainz"
            //   "hardcover"                    → "hardcover"
            source = PluginIdHelper.ToSource(excludePluginId);
            extId = rawExternalId;
        }
        else
        {
            // Fallback for callers that don't identify themselves (legacy/test paths).
            (source, extId) = ParseExternalId(rawExternalId);
        }

        var existing = await db.MediaExternalIds
            .FirstOrDefaultAsync(e => e.MediaItemId == mediaItemId && e.Source == source, ct);

        bool idChanged = false;

        if (existing is null)
        {
            db.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = mediaItemId,
                Source      = source,
                ExternalId  = extId,
            });
        }
        else if (existing.ExternalId != extId)
        {
            existing.ExternalId = extId;
            idChanged = true;
        }

        // When the canonical ID changes, invalidate sibling plugins so they
        // re-enrich against the corrected identity. Exclude the plugin that just
        // supplied the new ID — its row is updated by the caller after this returns.
        if (idChanged)
        {
            var query = db.MediaEnrichments.Where(e => e.MediaItemId == mediaItemId);
            if (excludePluginId is not null)
                query = query.Where(e => e.PluginId != excludePluginId);

            var rows = await query.ToListAsync(ct);
            foreach (var row in rows)
            {
                row.Status     = EnrichmentStatus.Pending;
                row.RetryCount = 0;
                row.ExternalId = null;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
