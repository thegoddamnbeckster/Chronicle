using Chronicle.Data;
using Chronicle.Plugins;

namespace Chronicle.Services;

/// <summary>
/// The Author -&gt; Series -&gt; Book counterpart of <see cref="IMovieCollectionService"/>'s
/// collection-stub machinery. Per-user direction (2026-09-28): Hardcover's own series data can't be
/// fully trusted (a book can move between "standalone" and "part of a series" on Hardcover's end, or
/// simply be wrong), so Chronicle must never duplicate a book across that change, a user's own manual
/// placement always wins over a later sync, and add/remove work exactly like a movie collection's own
/// (<see cref="IMovieCollectionService.ReparentIntoCollectionAsync"/> /
/// <see cref="IMovieCollectionService.UnparentFromCollectionAsync"/>) -- removing is NOT sticky: it
/// clears the manual marker too, the same as a movie leaving a collection, so the book is fully
/// auto-manageable again afterward.
/// </summary>
public interface IBookSeriesService
{
    /// <summary>
    /// After a series item is matched to a provider's series id, fetches the provider's full book
    /// list (<c>MediaMetadata.Results</c>) and reconciles Chronicle's own children under it:
    /// - A book already in the library anywhere under the SAME author (standalone or in another
    ///   series) is found by ExternalId, then by folder/file, then by normalized title+year --
    ///   REGARDLESS of its current level -- and reparented/renumbered in place. This is what stops a
    ///   book duplicating when its "standalone vs. in a series" status changes upstream.
    /// - A book with no match becomes a stub, numbered from the provider's position.
    /// - A stub no longer in the provider's list is removed.
    /// - Any book carrying the manual-placement marker (see <see cref="ReparentIntoSeriesAsync"/>) is
    ///   skipped entirely in both directions -- never moved, never removed, never renumbered by this
    ///   pass. The user's own decision always wins.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the series synced normally; <c>false</c> if the provider's returned name did not
    /// match the container (wrong match) -- stubs were purged and the bad ExternalId removed.
    /// </returns>
    Task<bool> EnsureSeriesStubsAsync(
        ChronicleDbContext db,
        Core.Models.MediaItem series,
        IMetadataProvider provider,
        CancellationToken ct = default,
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)>? allProviders = null);

    /// <summary>
    /// Moves a book that is not currently in any series into <paramref name="seriesId"/> (same
    /// author only) and marks the placement as user-chosen, so <see cref="EnsureSeriesStubsAsync"/>
    /// never reverts it. Resets the book's enrichment so it re-enriches in its new context.
    /// </summary>
    Task ReparentIntoSeriesAsync(
        ChronicleDbContext db, int bookId, int seriesId, CancellationToken ct = default);

    /// <summary>
    /// Moves a book out of its series back to a standalone book directly under its author -- NOT
    /// sticky: this also clears the manual-placement marker (mirroring
    /// <see cref="IMovieCollectionService.UnparentFromCollectionAsync"/> exactly), so the book is
    /// fully auto-manageable again on the next sync, the same as removing a movie from a collection.
    /// If the old series is left with no children, it is removed.
    /// </summary>
    Task RemoveFromSeriesAsync(ChronicleDbContext db, int bookId, CancellationToken ct = default);
}
