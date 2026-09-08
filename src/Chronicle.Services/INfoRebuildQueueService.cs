namespace Chronicle.Services;

/// <summary>One claimable unit of work, enough for a Kodi device to resolve the underlying
/// MediaItem in its OWN local VideoLibrary without any further round-trip to Chronicle.
/// Movies/shows carry Name+Year; episodes additionally carry the PARENT show's Name+Year (so
/// the addon can reuse its existing find_show_location()-style matcher) plus season/episode
/// numbers. KnownFileName (movies only -- shows/episodes are located by title or by
/// season+episode, never by filename) is the real video file's own basename WITH extension,
/// via FileIdentityJson.GetKnownFileName -- already stripped of its directory prefix
/// server-side (that method splits on both '\' and '/', so a Windows-style path from
/// Chronicle's own file scanner comes out right regardless of which OS is on either end),
/// letting the addon pass it straight to find_movie_location()'s own known_filename parameter
/// instead of re-deriving a basename from a raw path itself.</summary>
public record NfoRebuildQueueClaimDto(
    int QueueItemId,
    int MediaItemId,
    string Kind,
    string? Name,
    int? Year,
    string? ShowName,
    int? ShowYear,
    int? Season,
    int? Episode,
    string? KnownFileName
);

/// <summary>TotalPending is the queue's overall remaining-and-not-yet-claimed-by-anyone-else
/// depth AFTER this claim (i.e. including whatever this call itself just claimed, since those
/// aren't done yet either) -- lets a caller's progress UI show real "N remaining across the
/// whole backlog" numbers instead of resetting to "X of 25" every single batch.</summary>
public record NfoRebuildQueueClaimBatchDto(List<NfoRebuildQueueClaimDto> Items, int TotalPending);

/// <summary>One device's share of the queue, as of the moment GetStatusAsync ran. CompletedCount
/// is an approximation, not a ledger: the queue only ever records who currently/last CLAIMED a
/// row (ClaimedByKodiDeviceId), not who specifically completed it, so a row is attributed to
/// whichever device holds that claim at read time. Accurate for the overwhelmingly common case
/// (one device claims a batch and finishes it), but a row that changed hands after a lapsed lease
/// -- device A claimed it, its lease expired, device B claimed and finished it -- is attributed to
/// B, the device that actually did the work, which is the more useful answer anyway.
///
/// Host is included alongside DeviceName specifically because Name has no uniqueness constraint
/// and is entirely self-reported by each Kodi instance's own addon settings (KodiDevice's own
/// doc) -- confirmed live (2026-09-07) that two genuinely different devices can end up sharing a
/// name (one mis-registered under a stale/copy-pasted name), which without Host to disambiguate
/// looks indistinguishable from a bug ("this device is listed twice") rather than what it
/// actually is (a device that needs re-registering with the right name).</summary>
public record NfoRebuildQueueDeviceStatusDto(
    int KodiDeviceId, string DeviceName, string? Host, int ActiveClaims, int CompletedCount);

/// <summary>Snapshot of the whole cross-device rebuild queue for a status display -- see
/// NfoRebuildQueueItem's own doc for why the queue exists at all. PendingCount includes both
/// ActiveClaimCount (currently claimed, lease not yet lapsed) and whatever's left unclaimed;
/// TotalItems is CompletedCount + PendingCount, i.e. everything ever seeded, so a caller can
/// render a simple completed/total progress bar without a separate query.</summary>
public record NfoRebuildQueueStatusDto(
    int TotalItems, int CompletedCount, int PendingCount, int ActiveClaimCount,
    List<NfoRebuildQueueDeviceStatusDto> Devices
);

public interface INfoRebuildQueueService
{
    /// <summary>Claims up to batchSize eligible items (never claimed, or a previous claim's
    /// lease has lapsed) for kodiDeviceId, and marks them claimed with a fresh lease. Lazily
    /// tops up the queue first with any qualifying MediaItem that has no queue row at all yet
    /// (see EnsureSeededAsync) -- rate-limited internally so this stays cheap on every call.
    /// excludeKinds (values matching NfoRebuildQueueItem.Kind, e.g. "movie"/"tvshow"/"episode")
    /// lets a caller that has already determined it can't resolve a given kind locally this run
    /// (see Chronicle_Scraper's nfo_rebuild.py, which tracks a per-kind consecutive-failure
    /// streak) stop being handed more of it -- per-user report (2026-09-08): a device whose
    /// local library covers only a fraction of the shared catalog was claiming, failing to
    /// resolve, and releasing tens of thousands of items of the same doomed kind in a single
    /// run before this existed. Null or empty means no exclusion (every prior caller's
    /// behavior, unchanged).</summary>
    Task<NfoRebuildQueueClaimBatchDto> ClaimBatchAsync(int kodiDeviceId, int batchSize, TimeSpan lease,
        IReadOnlyCollection<string>? excludeKinds = null, CancellationToken ct = default);

    /// <summary>Marks one item done -- a no-op only if queueItemId doesn't exist or is already
    /// completed. Deliberately NOT gated on kodiDeviceId still being the row's current claimant:
    /// see the implementation's own doc for why silently discarding a late-but-genuine
    /// completion would be worse than accepting it.</summary>
    Task CompleteAsync(int queueItemId, int kodiDeviceId, CancellationToken ct = default);

    /// <summary>Releases a claim immediately (this device determined it can't process the item
    /// -- e.g. it doesn't have this file at all) so another device doesn't have to wait out the
    /// full lease before it becomes claimable again. Unlike CompleteAsync, this DOES require
    /// kodiDeviceId to still be the current claimant -- releasing a row this caller doesn't
    /// (or no longer) own would preempt whichever OTHER device is actually holding and working
    /// it right now.</summary>
    Task ReleaseAsync(int queueItemId, int kodiDeviceId, CancellationToken ct = default);

    /// <summary>Admin/manual "force full rebuild": clears every completed row and re-seeds the
    /// entire qualifying catalog from scratch, so the next round of claims covers everything
    /// again rather than only genuinely-new items. Returns how many rows are now pending.</summary>
    Task<int> ReseedAllAsync(CancellationToken ct = default);

    /// <summary>Read-only snapshot of the whole queue for a status display -- overall
    /// completed/pending/total counts plus a per-device breakdown. Does not seed or claim
    /// anything; safe to call as often as a UI wants to poll it.</summary>
    Task<NfoRebuildQueueStatusDto> GetStatusAsync(CancellationToken ct = default);
}
