using Chronicle.Core.Models;
using Chronicle.Data;

namespace Chronicle.Services;

public interface IPersonResolutionService
{
    /// <summary>
    /// Resolves (creating a stub if necessary) the "people"-type MediaItem this single credit
    /// belongs to, records any newly-supplied external id / headshot against that person, and
    /// writes the media_credits row itself. See PersonResolutionService's own doc for the full
    /// algorithm. Does NOT delete any existing credit rows for (titleMediaItemId, source) —
    /// callers doing a full re-sync of a title's credits (mirroring
    /// SyncOrchestrationService.FetchAndStoreCreditsAsync's own pattern) must clear those first,
    /// once, before calling this per credit.
    /// </summary>
    Task ResolveAndRecordCreditAsync(
        ChronicleDbContext db,
        int titleMediaItemId,
        string personName,
        string? externalPersonId,
        string source,
        string? profileImageUrl,
        string role,
        string? characterName,
        int? billingOrder,
        CancellationToken ct = default);

    /// <summary>
    /// Steps 1-4 only: resolves (creating a stub if necessary) the person, records a newly-
    /// supplied external id, and returns it -- WITHOUT writing/touching any media_credits row.
    /// For a caller that already has an existing credit row it just wants to point at the
    /// resolved person (the one-time startup backfill of pre-existing Trakt-sourced rows), not
    /// one adding a brand-new credit.
    /// </summary>
    Task<MediaItem> ResolvePersonOnlyAsync(
        ChronicleDbContext db, string personName, string? externalPersonId, string source,
        CancellationToken ct = default);

    /// <summary>
    /// Feed path 1 of the design doc's Section 1.5: a person's own enrichment (Wikipedia's bio
    /// photo, TMDB's own /person/{id} profile picture plus its full alternate-photo gallery,
    /// ...) returning one or more photos for the person item directly, as opposed to a photo
    /// arriving via someone else's credit list. No-op if <paramref name="photos"/> is empty
    /// (blank/null URLs are filtered out).
    /// </summary>
    Task RecordOwnPortraitAsync(
        ChronicleDbContext db, MediaItem person, IEnumerable<(string Url, string? ThumbnailUrl)> photos,
        string source, CancellationToken ct = default);
}
