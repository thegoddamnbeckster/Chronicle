using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Services;

public interface IMovieCollectionService
{
    /// <summary>
    /// Inspects the movie item's plugin metadata for <c>belongsToCollection</c> data
    /// from any registered plugin that stores it in the standard shape.
    /// If found, ensures a Collection parent MediaItem exists and re-parents the movie under it.
    /// No-op if the movie has no collection data or media type is not "movies".
    /// </summary>
    /// <param name="pluginId">
    /// When supplied, a Pending <c>MediaItemEnrichment</c> row is immediately inserted for any
    /// newly-created collection container, so the enrichment while-loop can pick it up on the
    /// next pass without waiting for the startup seed.
    /// </param>
    Task EnsureCollectionParentAsync(
        ChronicleDbContext db,
        MediaItem movieItem,
        string? pluginId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Backfill: iterates all movie items that have <c>belongsToCollection</c> in their
    /// stored metadata and calls <see cref="EnsureCollectionParentAsync"/> for each.
    /// Does not re-query any plugin API — uses only already-persisted metadata.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    Task ProcessAllExistingMovieCollectionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Finds Level-0 collection containers with the same name and media type (duplicates) and
    /// merges all children and ExternalIds into the oldest one, then deletes the extras.
    /// </summary>
    Task DeduplicateCollectionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Library-wide sweep removing collection containers that have no members left.
    ///
    /// RemoveOrphanedCollectionAsync only fires on the re-parenting paths (a movie moving to a
    /// different collection, or being unparented). A collection can also be emptied by routes
    /// that never touch those paths at all — its last member being merged away into an item
    /// elsewhere, deleted outright, or restored to a different parent by an unmerge — and those
    /// leave a childless container stranded in the library forever. Sweeping by end state
    /// catches every cause, including ones not yet thought of, instead of trying to hook each
    /// individual path.
    ///
    /// Only touches items that are genuinely collection containers (carrying a
    /// "collection:{id}" external ID) — never a childless standalone movie.
    /// </summary>
    /// <returns>How many empty containers were removed.</returns>
    Task<int> RemoveEmptyCollectionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Rebuilds a single collection: re-runs <see cref="EnsureCollectionParentAsync"/> on all
    /// non-stub children (moving any that don't belong here to their correct collection) then
    /// calls <see cref="EnsureCollectionStubsAsync"/> to fill in missing stubs.
    /// </summary>
    Task RebuildSingleCollectionAsync(
        int collectionId,
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)> providers,
        CancellationToken ct = default);

    /// <summary>
    /// Second pass of a full rebuild: iterates all Level-0 collection containers and calls
    /// <see cref="EnsureCollectionStubsAsync"/> for each, using the matching provider.
    /// </summary>
    Task CreateStubsForAllCollectionsAsync(
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)> providers,
        CancellationToken ct = default);

    /// <summary>
    /// After a collection container is found or created, fetches the full collection member list
    /// from the originating metadata provider and creates stub MediaItems for any movies that are
    /// not yet in the database. Stubs are flagged with <c>IsStub = true</c> so they can be
    /// hidden by users who opt out of collection stubs.
    /// </summary>
    /// <param name="db">The ambient DbContext (same as the caller's).</param>
    /// <param name="collection">The Level-0 collection container MediaItem.</param>
    /// <param name="provider">The metadata provider that populated the movie's collection data.</param>
    /// <returns>
    /// <c>true</c> if the collection synced normally; <c>false</c> if the provider returned a
    /// name that does not match the container (wrong TMDB match) — stubs were purged.
    /// </returns>
    Task<bool> EnsureCollectionStubsAsync(
        ChronicleDbContext db,
        MediaItem collection,
        IMetadataProvider provider,
        CancellationToken ct = default,
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)>? allProviders = null);

    /// <summary>
    /// Removes an item of any flat (non-hierarchical) media type from its collection container by
    /// setting ParentId = null and HierarchyLevel = 0, then resets enrichment to Pending.
    /// Deletes the container if it is left empty.
    /// No-op if the item is not a Level-1 item, or its media type has HierarchyLevels &gt; 1
    /// (e.g. TV, Music, anime) — those Level-1 items are structural, not collection members.
    /// </summary>
    Task UnparentFromCollectionAsync(ChronicleDbContext db, int itemId, CancellationToken ct = default);

    /// <summary>
    /// Manually places a standalone item into an existing collection container, setting
    /// ParentId = collectionId and HierarchyLevel = 1, then resets enrichment to Pending (mirrors
    /// <see cref="UnparentFromCollectionAsync"/>'s reset on the way out).
    /// Unlike <see cref="EnsureCollectionParentAsync"/>, this does not inspect the movie's own
    /// plugin metadata for a <c>belongsToCollection</c> match -- the caller (an admin, via the
    /// UI) has already decided the target collection explicitly. Works for any flat media type
    /// (HierarchyLevels == 1) — not just movies/fanedits/anime_movies — as long as both the item
    /// and the collection share the same media type. Types with a natural multi-level hierarchy
    /// (TV, Music, anime) are rejected, since grouping their Level-0 items this way would
    /// conflict with their real show/season or artist/album structure.
    /// </summary>
    /// <exception cref="MediaNotFoundException">Either id doesn't exist.</exception>
    /// <exception cref="InvalidOperationException">
    /// The movie is already parented somewhere, the target isn't a Level-0 item, either item's
    /// media type isn't flat, or the two items don't share the same media type.
    /// </exception>
    Task ReparentIntoCollectionAsync(
        ChronicleDbContext db, int movieId, int collectionId, CancellationToken ct = default);

    /// <summary>
    /// True if <paramref name="itemId"/> is acting as a collection container -- it has at least
    /// one child, or it carries a <c>collection:{id}</c> external ID (a brand-new container can
    /// have zero children for a moment between creation and stub-seeding). The single canonical
    /// check for "is this a collection, not a plain item" -- used anywhere that distinction
    /// gates a structural decision (merge eligibility, scraper candidate matching, enrichment's
    /// own re-parenting guard) so all of them agree with each other by construction instead of
    /// drifting via separately hand-rolled copies of the same two conditions.
    /// </summary>
    Task<bool> IsCollectionContainerAsync(ChronicleDbContext db, int itemId, CancellationToken ct = default);

    /// <summary>
    /// Batch form of <see cref="IsCollectionContainerAsync"/> — two queries over the whole
    /// candidate set instead of two queries per item. Use whenever the container check runs
    /// against a list rather than a single already-known item (e.g. filtering a scraper's
    /// title-match candidate pool).
    /// </summary>
    Task<HashSet<int>> GetCollectionContainerIdsAsync(
        ChronicleDbContext db, IReadOnlyCollection<int> candidateIds, CancellationToken ct = default);

    /// <summary>
    /// A poster to show for a collection that has none of its own -- the container's own
    /// PosterUrl (set only by a Rebuild pulling the provider's dedicated collection-level
    /// artwork) always wins when present; this is only ever consulted when that's empty.
    /// Walks the collection's children in the same year/name display order the collection
    /// detail page itself uses, and returns the first one with a resolved poster -- ownership
    /// (a real local file vs. a stub or a watch-history-only import) deliberately plays no part
    /// here: the goal is "always show a poster where one can be found," not "only ever show a
    /// poster for something you own." Null only when literally no child has any poster at all.
    /// The single implementation both the web collection page and the Kodi scraper's set-art
    /// fallback call, so "first available child poster" means the same thing everywhere.
    /// </summary>
    Task<string?> GetFallbackPosterAsync(ChronicleDbContext db, int collectionId, CancellationToken ct = default);
}
