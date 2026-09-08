using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Backs the cross-device NFO rebuild queue -- see NfoRebuildQueueItem's own doc for why this
/// exists (five Kodi instances sharing one library must not each independently re-walk and
/// re-rebuild the entire thing on every scan). ClaimBatchAsync/CompleteAsync/ReleaseAsync
/// implement a simple claim-with-lease pattern: a device claims a batch, processes it item by
/// item (still one-at-a-time on the Kodi side, per the existing "never leave more than one item
/// without an NFO" design in nfo_rebuild.py), and reports each one back.
///
/// Claiming is serialized through a process-wide lock (_claimLock below), not left as a bare
/// read-then-write race: a first version of this reasoned that a double-claim would be "self
/// correcting" (both devices write the same correct content, worst case one wasted write) --
/// that was wrong. The real failure mode is worse: the LOSING device still does the full
/// delete+refresh+wait pipeline and calls CompleteAsync, which used to no-op because the row's
/// ClaimedByKodiDeviceId had since moved to the winner -- silently discarding a real,
/// successful completion and leaving the item to be claimed and processed yet again by
/// someone else. The lock removes the race outright (adequate for Chronicle's
/// single-API-instance deployment model, same precedent as ScraperController's
/// _episodeResolutionLocks and this class's own _seedLock); CompleteAsync additionally no
/// longer requires the caller to be the CURRENT claimant (see its own doc) as defense in depth.
/// </summary>
public sealed class NfoRebuildQueueService(ChronicleDbContext db, ILogger<NfoRebuildQueueService> logger) : INfoRebuildQueueService
{

    // Process-wide (not per-request) throttle on the expensive "scan for never-queued items"
    // step -- claims can come in frequently (one per device per short poll), but the underlying
    // catalog only grows a handful of items between scans, so re-scanning tens of thousands of
    // MediaItems on every single claim call would be pure waste. Static, matching
    // ScraperController's own _episodeResolutionLocks precedent for "adequate for Chronicle's
    // single-API-instance deployment model."
    private static DateTime _lastSeedCheck = DateTime.MinValue;
    private static readonly TimeSpan SeedThrottle = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim _seedLock = new(1, 1);

    /// <summary>Serializes the whole claim critical section (select-eligible + mark-claimed)
    /// so two concurrent ClaimBatchAsync calls can never select the same row -- see the class's
    /// own doc for why a bare read-then-write here was a real bug, not just a benign race.
    /// Adequate for Chronicle's single-API-instance deployment model, same as _seedLock.</summary>
    private static readonly SemaphoreSlim _claimLock = new(1, 1);

    // How long a device that just released an item is excluded from reclaiming that SAME item
    // itself -- see NfoRebuildQueueItem.LastReleasedByKodiDeviceId's own doc for the exact
    // failure this closes (a device whose local library is a strict subset of the shared
    // catalog spinning forever on the same contiguous run of items it can never find). Long
    // enough that it can't just immediately re-claim the row on its very next batch (the whole
    // point), short enough that if the device's own library later grows to include the item
    // (a rescan, a new mount), it naturally becomes eligible again without needing a manual
    // force-reseed. Does not affect any OTHER device's ability to claim the row -- a device that
    // actually has the file should be able to pick it up immediately, not wait out someone
    // else's cooldown.
    private static readonly TimeSpan ReleaseCooldown = TimeSpan.FromHours(2);

    /// <summary>Test-only: forces the next EnsureSeededAsync call to actually run, bypassing the
    /// throttle. Needed because the throttle is process-static (see its own comment above) --
    /// without this, a test running after any other test that already exercised claiming in the
    /// same process would see a skipped seed and an empty claim. Same "internal for unit tests"
    /// precedent as TaskSchedulerService.SeedTasksAsync/TickAsync.</summary>
    internal static void ResetSeedThrottleForTests() => _lastSeedCheck = DateTime.MinValue;

    public async Task<NfoRebuildQueueClaimBatchDto> ClaimBatchAsync(
        int kodiDeviceId, int batchSize, TimeSpan lease,
        IReadOnlyCollection<string>? excludeKinds = null, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);

        List<NfoRebuildQueueItem> candidates;
        await _claimLock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var cooldownCutoff = now - ReleaseCooldown;
            var query = db.NfoRebuildQueue
                .Where(q => q.CompletedAt == null && (q.ClaimedByKodiDeviceId == null || q.LeaseExpiresAt < now))
                // Excludes only THIS device's own still-cooling-down releases -- see
                // ReleaseCooldown's own doc. Any other device is unaffected by this clause
                // (a row this device can't reclaim yet may still be claimed by someone else in
                // the very same batch).
                .Where(q => q.LastReleasedByKodiDeviceId != kodiDeviceId || q.LastReleasedAt < cooldownCutoff);

            // See this method's own interface doc for why -- a device that already proved this
            // run it can't resolve a kind locally shouldn't keep being handed more of it.
            if (excludeKinds is { Count: > 0 })
                query = query.Where(q => !excludeKinds.Contains(q.Kind));

            candidates = await query
                .OrderBy(q => q.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (candidates.Count == 0)
                return new NfoRebuildQueueClaimBatchDto([], await db.NfoRebuildQueue.CountAsync(q => q.CompletedAt == null, ct));

            foreach (var c in candidates)
            {
                c.ClaimedByKodiDeviceId = kodiDeviceId;
                c.ClaimedAt             = now;
                c.LeaseExpiresAt        = now + lease;
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _claimLock.Release();
        }

        var dtos = await BuildClaimDtosAsync(candidates, ct);
        var totalPending = await db.NfoRebuildQueue.CountAsync(q => q.CompletedAt == null, ct);
        return new NfoRebuildQueueClaimBatchDto(dtos, totalPending);
    }

    /// <summary>Deliberately does NOT require kodiDeviceId to still be the row's current
    /// ClaimedByKodiDeviceId -- a caller reaching this point already did the real work
    /// (delete+refresh+wait) and is reporting a genuine success. Rejecting that because the
    /// row's ownership happened to move on since (e.g. this call arrived unusually late, past
    /// its own lease) would silently discard a correct completion and leave the item to be
    /// claimed and redone by someone else for no reason. The only thing that matters is that
    /// SOME device confirms the item; kodiDeviceId is accepted as a parameter (rather than
    /// dropped) purely so future logging/metrics can still see who did the work.</summary>
    public async Task CompleteAsync(int queueItemId, int kodiDeviceId, CancellationToken ct = default)
    {
        var row = await db.NfoRebuildQueue.FindAsync([queueItemId], ct);
        if (row is null || row.CompletedAt is not null) return;
        row.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReleaseAsync(int queueItemId, int kodiDeviceId, CancellationToken ct = default)
    {
        var row = await db.NfoRebuildQueue.FindAsync([queueItemId], ct);
        if (row is null || row.ClaimedByKodiDeviceId != kodiDeviceId) return;
        row.ClaimedByKodiDeviceId = null;
        row.ClaimedAt             = null;
        row.LeaseExpiresAt        = null;
        // Recorded so ClaimBatchAsync's eligibility query can keep THIS device from immediately
        // reclaiming the very row it just gave up on -- see LastReleasedByKodiDeviceId's own
        // doc and ReleaseCooldown for why.
        row.LastReleasedByKodiDeviceId = kodiDeviceId;
        row.LastReleasedAt             = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> ReseedAllAsync(CancellationToken ct = default)
    {
        // Reset every row back to pending rather than delete+reinsert -- keeps the unique
        // (MediaItemId) index intact throughout and avoids a window where EnsureSeededAsync
        // (racing on another request) could see a momentarily-missing row and insert a
        // duplicate for the same item. Plain tracked-entity mutation, not ExecuteUpdateAsync --
        // this is an admin-triggered, deliberately-rare "force everything" action (not a
        // per-request hot path), so the cost of loading every row into the change tracker is
        // acceptable, and doing it this way keeps the method actually unit-testable (EF's
        // InMemory provider used by NfoRebuildQueueServiceTests doesn't support ExecuteUpdate
        // at all).
        var all = await db.NfoRebuildQueue.ToListAsync(ct);
        foreach (var row in all)
        {
            row.CompletedAt                = null;
            row.ClaimedByKodiDeviceId      = null;
            row.ClaimedAt                  = null;
            row.LeaseExpiresAt             = null;
            // A force-reseed means "everyone retry everything" -- any standing release
            // cooldown (see ReleaseCooldown's own doc) would otherwise keep blocking the
            // device that hit it from claiming this row again for up to 2 more hours,
            // defeating the whole point of an explicit force-rebuild.
            row.LastReleasedByKodiDeviceId = null;
            row.LastReleasedAt             = null;
        }
        await db.SaveChangesAsync(ct);

        _lastSeedCheck = DateTime.MinValue; // force the next EnsureSeededAsync to actually run
        await EnsureSeededAsync(ct);

        return await db.NfoRebuildQueue.CountAsync(q => q.CompletedAt == null, ct);
    }

    public async Task<NfoRebuildQueueStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        // Same lazy top-up ClaimBatchAsync already does (throttled, see EnsureSeededAsync's own
        // doc) -- without it, a fresh install or one where no Kodi device has ever polled yet
        // would show an empty queue and read as "nothing to rebuild" rather than "hasn't been
        // seeded yet," even though the library itself has plenty of qualifying items.
        await EnsureSeededAsync(ct);

        var now = DateTime.UtcNow;

        var totalItems = await db.NfoRebuildQueue.CountAsync(ct);
        var completedCount = await db.NfoRebuildQueue.CountAsync(q => q.CompletedAt != null, ct);
        var activeClaimCount = await db.NfoRebuildQueue.CountAsync(
            q => q.CompletedAt == null && q.ClaimedByKodiDeviceId != null && q.LeaseExpiresAt >= now, ct);

        // Grouped in the database (cheap even at tens of thousands of rows -- both filters hit
        // idx_nfo_rebuild_queue_claimable), then joined to device names in memory: there's no FK
        // from ClaimedByKodiDeviceId to KodiDevice (see this table's own mapping comment -- a
        // device can be deleted/re-registered without this queue needing to cascade), so a
        // straight SQL join could silently drop a device that's since gone away instead of still
        // reporting the work it's credited with.
        var perDeviceCounts = await db.NfoRebuildQueue
            .Where(q => q.ClaimedByKodiDeviceId != null)
            .GroupBy(q => q.ClaimedByKodiDeviceId!.Value)
            .Select(g => new
            {
                KodiDeviceId = g.Key,
                ActiveClaims = g.Count(q => q.CompletedAt == null && q.LeaseExpiresAt >= now),
                CompletedCount = g.Count(q => q.CompletedAt != null),
            })
            .Where(g => g.ActiveClaims > 0 || g.CompletedCount > 0)
            .ToListAsync(ct);

        var deviceIds = perDeviceCounts.Select(d => d.KodiDeviceId).ToList();
        var devicesById = await db.KodiDevices
            .Where(d => deviceIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => new { d.Name, d.Host }, ct);

        var devices = perDeviceCounts
            .Select(d =>
            {
                var found = devicesById.TryGetValue(d.KodiDeviceId, out var info);
                return new NfoRebuildQueueDeviceStatusDto(
                    d.KodiDeviceId, found ? info!.Name : "(deleted device)", found ? info!.Host : null,
                    d.ActiveClaims, d.CompletedCount);
            })
            .OrderByDescending(d => d.CompletedCount)
            .ThenByDescending(d => d.ActiveClaims)
            .ToList();

        return new NfoRebuildQueueStatusDto(
            totalItems, completedCount, totalItems - completedCount, activeClaimCount, devices);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastSeedCheck < SeedThrottle) return;

        // A second caller arriving while the first is already mid-scan should just wait for it
        // rather than duplicate the same expensive work -- WaitAsync/Release, not a bare
        // "first one wins, others skip" check, since a skipped caller here would otherwise claim
        // against a queue that hasn't actually been topped up yet.
        await _seedLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _lastSeedCheck < SeedThrottle) return; // someone else just did it
            _lastSeedCheck = DateTime.UtcNow;

            var typesByName = await db.MediaTypes
                .Where(t => NfoKindHelper.MovieLikeTypeNames.Contains(t.Name) || NfoKindHelper.ShowLikeTypeNames.Contains(t.Name))
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
            if (typesByName.Count == 0) return;

            var movieTypeIds = typesByName.Where(kv => NfoKindHelper.MovieLikeTypeNames.Contains(kv.Value)).Select(kv => kv.Key).ToList();
            var showTypeIds  = typesByName.Where(kv => NfoKindHelper.ShowLikeTypeNames.Contains(kv.Value)).Select(kv => kv.Key).ToList();

            // Collection containers (e.g. "Toy Story Collection") are movie-typed and sit at
            // HierarchyLevel 0 exactly like a standalone movie, so the type filter alone can't
            // tell them apart -- confirmed live (2026-09-06): every collection container in the
            // library was being queued here, and since a container has no video file/folder of
            // its own, movie_art_sync.find_movie_location() can never resolve one, so the addon
            // burned its full 60s wait timeout on every single one for nothing before releasing
            // it. Same detection MetadataEnrichmentService.MarkCollectionContainersPendingAsSkippedAsync
            // already uses: a container is identified by its own "collection:" external id, not
            // by a real per-provider match.
            var collectionContainerIds = (await db.MediaExternalIds
                .Where(e => e.ExternalId.StartsWith("collection:"))
                .Select(e => e.MediaItemId)
                .Distinct()
                .ToListAsync(ct)).ToHashSet();

            // Prunes rows already queued for a container BEFORE this fix landed, not just new
            // ones going forward -- otherwise every already-installed deployment's backlog would
            // carry this dead weight until an admin ran ReseedAllAsync by hand. Deleted outright
            // (not marked Skipped/CompletedAt) since a container can never legitimately appear
            // here again to re-trigger this same check; unlike EnsureSeededAsync's own additive
            // "top up" design, cleanup only ever needs to run once per stale row, and re-running
            // this Where/Any query every throttle interval against an already-clean queue is
            // cheap once the initial backlog is gone.
            if (collectionContainerIds.Count > 0)
            {
                var staleContainerRows = await db.NfoRebuildQueue
                    .Where(q => collectionContainerIds.Contains(q.MediaItemId))
                    .ToListAsync(ct);
                if (staleContainerRows.Count > 0)
                {
                    db.NfoRebuildQueue.RemoveRange(staleContainerRows);
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation(
                        "NfoRebuildQueueService: pruned {Count} stale collection-container row(s) " +
                        "from the rebuild queue -- these can never resolve to a real local file",
                        staleContainerRows.Count);
                }
            }

            // Id + HierarchyLevel only -- this can be tens of thousands of rows, no need to pull
            // full MediaItem entities just to classify them.
            var movieIds = await db.MediaItems
                .Where(m => movieTypeIds.Contains(m.MediaTypeId))
                .Select(m => m.Id)
                .ToListAsync(ct);
            movieIds.RemoveAll(collectionContainerIds.Contains);

            // Number != null on the episode side excludes a numberless episode (a scan/matching
            // gap upstream -- see ScraperController.GetEpisodes' own identical exclusion and its
            // doc for why) from ever entering the queue at all: with no real episode number,
            // get_episode(tvshowid, season, None) can never match a real Kodi episode, so every
            // device that claimed one would release it right back, forever -- an unproductive
            // claim/release loop with no way to ever complete. Shows (HierarchyLevel == 0) have
            // no equivalent "Number" concept to gate on.
            var showEpisodeRows = await db.MediaItems
                .Where(m => showTypeIds.Contains(m.MediaTypeId)
                         && (m.HierarchyLevel == 0 || (m.HierarchyLevel == 2 && m.Number != null)))
                .Select(m => new { m.Id, m.HierarchyLevel })
                .ToListAsync(ct);

            var existingIds = (await db.NfoRebuildQueue.Select(q => q.MediaItemId).ToListAsync(ct)).ToHashSet();

            var now = DateTime.UtcNow;
            var toInsert = new List<NfoRebuildQueueItem>();
            foreach (var id in movieIds)
                if (!existingIds.Contains(id))
                    toInsert.Add(new NfoRebuildQueueItem { MediaItemId = id, Kind = "movie", EnqueuedAt = now });
            foreach (var row in showEpisodeRows)
                if (!existingIds.Contains(row.Id))
                    toInsert.Add(new NfoRebuildQueueItem
                    {
                        MediaItemId = row.Id,
                        Kind        = row.HierarchyLevel == 0 ? "tvshow" : "episode",
                        EnqueuedAt  = now,
                    });

            if (toInsert.Count > 0)
            {
                db.NfoRebuildQueue.AddRange(toInsert);
                await db.SaveChangesAsync(ct);
            }
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private async Task<List<NfoRebuildQueueClaimDto>> BuildClaimDtosAsync(
        List<NfoRebuildQueueItem> candidates, CancellationToken ct)
    {
        var mediaItemIds = candidates.Select(c => c.MediaItemId).ToList();
        var itemsById = await db.MediaItems
            .Where(m => mediaItemIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);

        // Auto-complete any claimed row whose underlying MediaItem is gone (deleted/merged since
        // being queued) -- it can never resolve, and leaving it claimed-but-dangling would just
        // strand it past its own lease for no reason.
        var missingIds = candidates.Where(c => !itemsById.ContainsKey(c.MediaItemId)).ToList();
        if (missingIds.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var c in missingIds) c.CompletedAt = now;
            await db.SaveChangesAsync(ct);
        }

        var episodeItems = candidates
            .Where(c => itemsById.ContainsKey(c.MediaItemId) && c.Kind == "episode")
            .Select(c => itemsById[c.MediaItemId])
            .ToList();
        var seasonIds = episodeItems.Where(e => e.ParentId.HasValue).Select(e => e.ParentId!.Value).Distinct().ToList();
        var seasonsById = await db.MediaItems.Where(m => seasonIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var showIds = seasonsById.Values.Where(s => s.ParentId.HasValue).Select(s => s.ParentId!.Value).Distinct().ToList();
        var showsById = await db.MediaItems.Where(m => showIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);

        var result = new List<NfoRebuildQueueClaimDto>();
        foreach (var c in candidates)
        {
            if (!itemsById.TryGetValue(c.MediaItemId, out var item)) continue; // just auto-completed above

            // Movies only -- shows are located by title, episodes by season+episode number
            // under the resolved show, neither of which uses a filename at all.
            var knownFileName = c.Kind == "movie" ? FileIdentityJson.GetKnownFileName(item.MetadataJson) : null;

            if (c.Kind == "episode")
            {
                var season = item.ParentId.HasValue && seasonsById.TryGetValue(item.ParentId.Value, out var s) ? s : null;
                var show = season?.ParentId is int showId && showsById.TryGetValue(showId, out var sh) ? sh : null;
                result.Add(new NfoRebuildQueueClaimDto(
                    c.Id, c.MediaItemId, c.Kind, item.Name, item.Year,
                    show?.Name, show?.Year, season?.Number, item.Number, KnownFileName: null));
            }
            else
            {
                result.Add(new NfoRebuildQueueClaimDto(
                    c.Id, c.MediaItemId, c.Kind, item.Name, item.Year,
                    null, null, null, null, knownFileName));
            }
        }
        return result;
    }
}
