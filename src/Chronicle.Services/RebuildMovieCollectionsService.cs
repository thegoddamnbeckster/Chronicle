using Chronicle.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Background task that re-processes all existing movie metadata to correct collection groupings
/// and fill in stub entries for missing collection members.
///
/// Pass 1 — re-parenting: reads <c>belongsToCollection</c> from each movie's stored metadata_json
/// and ensures it is parented under the correct Level-0 collection container. No external API
/// calls; purely local. Fixes movies enriched before the collection feature existed and movies
/// incorrectly grouped due to a bad TMDB match that was later corrected via Fix Match.
///
/// Pass 2 — stubs: iterates every collection container and ensures stub entries exist for
/// collection members not yet in the database (calls the TMDB plugin's collection endpoint).
/// </summary>
public sealed class RebuildMovieCollectionsService : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _log = Log.ForContext<RebuildMovieCollectionsService>();

    public RebuildMovieCollectionsService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string TaskId      => "rebuild_movie_collections";
    public string DisplayName => "Rebuild Movie Collections";
    public string Description => "Re-parents all movies into their correct collections and creates stub entries for missing collection members using stored metadata — no re-fetch required for pass 1.";
    public string DefaultCron => "0 4 * * 0"; // weekly, Sunday 4 AM — manual trigger is the normal path

    async Task IScheduledTask.ExecuteAsync(CancellationToken ct)
    {
        _log.Information("RebuildMovieCollections: starting");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var collectionService = scope.ServiceProvider.GetRequiredService<IMovieCollectionService>();
        var registry          = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        // Pass 0: merge any duplicate collection containers (same name, same media type)
        await collectionService.DeduplicateCollectionsAsync(ct);

        // Pass 1: re-parent all movies based on stored belongsToCollection data
        await collectionService.ProcessAllExistingMovieCollectionsAsync(ct);

        // Pass 1b: re-parent may have created new containers for collections whose ExternalId
        // was previously cleared — deduplicate again to merge those with any survivors.
        await collectionService.DeduplicateCollectionsAsync(ct);

        // Pass 2: create stubs for missing collection members (requires live plugin)
        var providers = registry.GetMetadataProviderEntries()
            .Select(e => (e.PluginId, e.Provider))
            .ToList();

        await collectionService.CreateStubsForAllCollectionsAsync(providers, ct);

        // Pass 3: sweep away containers left with no members. Runs last so it sees the final
        // state after re-parenting and stub creation — a collection that looks empty mid-run
        // may well have members again by the time pass 2 finishes.
        var removed = await collectionService.RemoveEmptyCollectionsAsync(ct);
        if (removed > 0)
            _log.Information("RebuildMovieCollections: removed {Count} empty collection(s)", removed);

        _log.Information("RebuildMovieCollections: complete");
    }
}
