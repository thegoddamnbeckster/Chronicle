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
        CancellationToken ct = default);
}
