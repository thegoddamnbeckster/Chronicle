using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;

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
public sealed class NfoRebuildQueueService(ChronicleDbContext db) : INfoRebuildQueueService
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

    /// <summary>Test-only: forces the next EnsureSeededAsync call to actually run, bypassing the
    /// throttle. Needed because the throttle is process-static (see its own comment above) --
    /// without this, a test running after any other test that already exercised claiming in the
    /// same process would see a skipped seed and an empty claim. Same "internal for unit tests"
    /// precedent as TaskSchedulerService.SeedTasksAsync/TickAsync.</summary>
    internal static void ResetSeedThrottleForTests() => _lastSeedCheck = DateTime.MinValue;

    public async Task<NfoRebuildQueueClaimBatchDto> ClaimBatchAsync(
        int kodiDeviceId, int batchSize, TimeSpan lease, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);

        List<NfoRebuildQueueItem> candidates;
        await _claimLock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            candidates = await db.NfoRebuildQueue
                .Where(q => q.CompletedAt == null && (q.ClaimedByKodiDeviceId == null || q.LeaseExpiresAt < now))
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
            row.CompletedAt             = null;
            row.ClaimedByKodiDeviceId   = null;
            row.ClaimedAt               = null;
            row.LeaseExpiresAt          = null;
        }
        await db.SaveChangesAsync(ct);

        _lastSeedCheck = DateTime.MinValue; // force the next EnsureSeededAsync to actually run
        await EnsureSeededAsync(ct);

        return await db.NfoRebuildQueue.CountAsync(q => q.CompletedAt == null, ct);
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

            // Id + HierarchyLevel only -- this can be tens of thousands of rows, no need to pull
            // full MediaItem entities just to classify them.
            var movieIds = await db.MediaItems
                .Where(m => movieTypeIds.Contains(m.MediaTypeId))
                .Select(m => m.Id)
                .ToListAsync(ct);

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
