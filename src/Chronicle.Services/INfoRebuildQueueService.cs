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

public interface INfoRebuildQueueService
{
    /// <summary>Claims up to batchSize eligible items (never claimed, or a previous claim's
    /// lease has lapsed) for kodiDeviceId, and marks them claimed with a fresh lease. Lazily
    /// tops up the queue first with any qualifying MediaItem that has no queue row at all yet
    /// (see EnsureSeededAsync) -- rate-limited internally so this stays cheap on every call.</summary>
    Task<NfoRebuildQueueClaimBatchDto> ClaimBatchAsync(int kodiDeviceId, int batchSize, TimeSpan lease, CancellationToken ct = default);

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
}
