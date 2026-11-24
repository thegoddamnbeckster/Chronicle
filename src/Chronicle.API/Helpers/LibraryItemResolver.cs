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
        var all = ResolveAll(libraryByExternalId, primaryId, primarySource, contributing);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>Every library item the candidate matches, the candidate's own id first. The same show can be held under more than
    /// one media type, which <see cref="Resolve"/> alone cannot tell.</summary>
    public static IReadOnlyList<int> ResolveAll(
        IReadOnlyDictionary<string, List<MediaExternalId>> libraryByExternalId,
        string primaryId,
        string? primarySource,
        IReadOnlyList<ContributingExternalId>? contributing)
    {
        var found = new List<int>();

        if (libraryByExternalId.TryGetValue(primaryId, out var rows))
            found.AddRange(rows.Where(r => string.Equals(r.Source, primarySource, StringComparison.OrdinalIgnoreCase)).Select(r => r.MediaItemId));

        foreach (var c in contributing ?? [])
        {
            if (!libraryByExternalId.TryGetValue(c.ExternalId, out var cRows)) continue;
            found.AddRange(cRows.Where(r => string.Equals(r.Source, c.Source, StringComparison.OrdinalIgnoreCase)).Select(r => r.MediaItemId));
        }

        return found.Distinct().ToList();
    }

    /// <summary>
    /// Whether a result belongs in a search for one media type. A result the library already holds ONLY under other types (The
    /// Daily Show, which is a TV show, found from the Anime tab) is not a result for this type and is left out, whatever the
    /// types are: each is its own, so a Fan Edit never turns up among Movies nor a Movie among Anime Movies. A result the library
    /// does not hold, or holds under this type too, stays.
    /// </summary>
    /// <param name="matchedItemIds">The library items the result matches (<see cref="ResolveAll"/>).</param>
    /// <param name="typeNameByItemId">The media type name of each library item.</param>
    /// <param name="searchedTypeNames">The database names of the type being searched.</param>
    public static bool BelongsInSearchOf(
        IReadOnlyList<int> matchedItemIds,
        IReadOnlyDictionary<int, string> typeNameByItemId,
        IReadOnlySet<string> searchedTypeNames) =>
        matchedItemIds.Count == 0
        || matchedItemIds.Any(id => !typeNameByItemId.TryGetValue(id, out var type) || searchedTypeNames.Contains(type));
}
