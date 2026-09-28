using Chronicle.Data;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Runs <see cref="IBookSeriesService.EnsureSeriesStubsAsync"/> for every series Chronicle has
/// matched to a Hardcover series id. Per-user direction (2026-09-28): this is deliberately its own
/// pass rather than running inline per-book the way <see cref="MovieCollectionService"/>'s does for
/// movies, and it is chained to run right after the Hardcover plugin's own "fetch-missing-metadata"
/// background task finishes (see TaskSchedulerService's own call site) rather than on its own
/// schedule -- there is no point reconciling series membership before that pass has had a chance to
/// attach/refresh the series ids it acts on.
/// </summary>
public sealed class HardcoverSeriesReconcileService(
    IServiceScopeFactory scopeFactory,
    ILogger<HardcoverSeriesReconcileService> logger)
{
    private const string HardcoverPluginId = "hardcover";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();
        var bookSeriesService = scope.ServiceProvider.GetRequiredService<IBookSeriesService>();

        var provider = registry.GetMetadataProvider(HardcoverPluginId);
        if (provider is null)
        {
            logger.LogInformation("Hardcover series reconcile: Hardcover plugin is not installed/enabled, nothing to do");
            return;
        }
        var allProviders = registry.GetMetadataProviderEntries()
            .Select(e => (e.PluginId, e.Provider)).ToList();

        // Every level-1 item (a series, in an Author->Series->Book type) that Hardcover has
        // claimed as a series -- see BookSeriesService's own doc for why the ExternalId is
        // stored as the full "hardcover:series:{id}" string rather than a bare id.
        var seriesIds = await db.MediaItems
            .Where(m => m.HierarchyLevel == 1 && m.ParentId != null && m.MediaType!.HierarchyLevels >= 3 &&
                        db.MediaExternalIds.Any(e => e.MediaItemId == m.Id && e.Source == "hardcover" &&
                                                      e.ExternalId.Contains("series:")))
            .Select(m => m.Id)
            .ToListAsync(ct);

        var ok = 0;
        var badMatch = 0;
        foreach (var seriesId in seriesIds)
        {
            ct.ThrowIfCancellationRequested();
            var series = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == seriesId, ct);
            if (series is null) continue;
            try
            {
                var succeeded = await bookSeriesService.EnsureSeriesStubsAsync(db, series, provider, ct, allProviders);
                if (succeeded) ok++; else badMatch++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Hardcover series reconcile: series {SeriesId} \"{Name}\" failed, left unchanged",
                    seriesId, series.Name);
            }
            db.ChangeTracker.Clear();
        }

        logger.LogInformation(
            "Hardcover series reconcile: {Total} series checked -- {Ok} synced, {Bad} had a wrong/stale match removed",
            seriesIds.Count, ok, badMatch);
    }
}
