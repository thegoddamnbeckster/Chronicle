using Chronicle.Core.Models;
using Chronicle.Services;

namespace Chronicle.API.Helpers;

/// <summary>
/// Resolves a metadata search candidate onto an already-in-library MediaItem by external ID,
/// for FileScanController.SearchMetadata's "In Library" badge. Extracted from that method so
/// the matching rule itself is directly unit-testable without spinning up the full HTTP +
/// plugin pipeline.
///
/// A bare ExternalId string is not guaranteed unique ACROSS different providers' own ID
/// spaces (e.g. two providers each using a "movie:{n}" convention with unrelated numbering
/// could coincidentally collide on the same digits). Confirmed live (2026-08-31): a stray
/// non-tmdb row whose ExternalId happened to equal "movie:11649" (TMDB's real id for the 1987
/// Masters of the Universe) made the 1987 search result's "In Library" badge resolve to an
/// unrelated 2026 remake that owned that row under a different Source. Every id -- the
/// candidate's own primary id AND each contributing id from other providers -- is therefore
/// matched on (Source, ExternalId) together, never ExternalId alone. Contributing ids carry
/// their own Source explicitly (set at search-merge time in FileScanService.SearchMetadataAsync,
/// from the actual provider that returned them) rather than one being guessed from the id
/// string's prefix convention.
/// </summary>
public static class LibraryItemResolver
{
    /// <param name="libraryByExternalId">Every media_external_ids row (for the current user's
    /// library) whose ExternalId matches any candidate's own id or contributing id, grouped by
    /// ExternalId -- a group can contain rows from more than one Source and/or MediaItem.</param>
    /// <param name="primaryId">The search candidate's own ExternalId.</param>
    /// <param name="primarySource">The search candidate's own Source (e.g. "tmdb"). Matched
    /// against each candidate row's Source for <paramref name="primaryId"/> -- a row under a
    /// different Source that merely shares the same ExternalId string is never accepted here.</param>
    /// <param name="contributing">Other providers' ids for the same real-world item (already
    /// title+year-validated by FileScanService.SearchMetadataAsync's own dedup pass before
    /// reaching here), each carrying its own Source. Matched the same (Source, ExternalId) way
    /// as <paramref name="primaryId"/> -- a row under a different Source than the one that
    /// actually returned this contributing id is never accepted, closing the same collision
    /// class for these ids too.</param>
    public static int? Resolve(
        IReadOnlyDictionary<string, List<MediaExternalId>> libraryByExternalId,
        string primaryId,
        string? primarySource,
        IReadOnlyList<ContributingExternalId>? contributing)
    {
        if (libraryByExternalId.TryGetValue(primaryId, out var rows))
        {
            var sourceMatch = rows.FirstOrDefault(
                r => string.Equals(r.Source, primarySource, StringComparison.OrdinalIgnoreCase));
            if (sourceMatch is not null) return sourceMatch.MediaItemId;
        }

        foreach (var c in contributing ?? [])
        {
            if (!libraryByExternalId.TryGetValue(c.ExternalId, out var cRows)) continue;
            var sourceMatch = cRows.FirstOrDefault(
                r => string.Equals(r.Source, c.Source, StringComparison.OrdinalIgnoreCase));
            if (sourceMatch is not null) return sourceMatch.MediaItemId;
        }

        return null;
    }
}
