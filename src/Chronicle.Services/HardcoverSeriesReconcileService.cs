using System.Net;
using System.Text.Json;
using Chronicle.Core.Models;
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

    // AppSetting holding {seriesItemId: lastSyncedUtc}. Root-caused live (2026-09-28): this sweep
    // used to re-fetch EVERY matched series (~830, one Hardcover request each) after every
    // fetch-missing-metadata run -- four times a day -- which alone was ~3,300 of Free-tier
    // Hardcover's 5,000 daily requests and, with the enrichment backlog, exhausted the quota until
    // 00:00 UTC. A series' book list changes rarely, so each series is now re-checked at most once
    // per StaleAfter, oldest first, and at most MaxPerRun per pass, spreading the cost out.
    internal const string LastSyncedSettingKey = "hardcover.series_reconcile.last_synced";
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);
    internal const int MaxPerRun = 150;
    // A series whose sync FAILED (not a throttle) is retried after this, not on the very next pass:
    // otherwise a handful of permanently-failing series sort first every run and starve the rest.
    internal static readonly TimeSpan FailedRetryAfter = TimeSpan.FromDays(1);
    // Stop the pass after this many failures in a row -- the provider or token is broken, and every
    // further series would just repeat the same doomed (and possibly slow, backed-off) request.
    internal const int MaxConsecutiveFailures = 3;

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

        const int PageSize = 200; // matches MovieCollectionService's own BulkBatchSize convention

        // Every level-1 item (a series, in an Author->Series->Book type) that Hardcover has
        // claimed as a series -- see BookSeriesService's own doc for why the ExternalId is
        // stored as the full "hardcover:series:{id}" string rather than a bare id.
        var seriesIds = await db.MediaItems
            .Where(m => m.HierarchyLevel == 1 && m.ParentId != null && m.MediaType!.HierarchyLevels >= 3 &&
                        db.MediaExternalIds.Any(e => e.MediaItemId == m.Id && e.Source == "hardcover" &&
                                                      e.ExternalId.Contains("series:")))
            .Select(m => m.Id)
            .ToListAsync(ct);

        var lastSynced = await LoadLastSyncedAsync(db, ct);
        var now = DateTime.UtcNow;
        var due = seriesIds
            .Where(id => !lastSynced.TryGetValue(id, out var at) || now - at >= StaleAfter)
            .OrderBy(id => lastSynced.TryGetValue(id, out var at) ? at : DateTime.MinValue)
            .Take(MaxPerRun)
            .ToList();

        var ok = 0;
        var badMatch = 0;
        var rateLimited = false;
        var consecutiveFailures = 0;
        try
        {
            foreach (var chunk in due.Chunk(PageSize))
            {
                // One query per PAGE, not per series -- an author with a couple dozen series (a real
                // shape, per Hardcover's own author-series listing seen live) used to cost one extra
                // round trip each; ChangeTracker.Clear() still runs once per page (not once per item)
                // so a large sweep across many authors doesn't grow the tracker unbounded either.
                var series = await db.MediaItems.Where(m => chunk.Contains(m.Id)).ToListAsync(ct);
                foreach (var seriesItem in series)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var succeeded = await bookSeriesService.EnsureSeriesStubsAsync(db, seriesItem, provider, ct, allProviders);
                        if (succeeded) ok++; else badMatch++;
                        lastSynced[seriesItem.Id] = DateTime.UtcNow;
                        consecutiveFailures = 0;
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        // Hardcover is throttling us (or the daily quota is gone): every further series
                        // would fail the same way, so stop the sweep here. This series is not marked
                        // synced, so it is first in line next time.
                        logger.LogWarning("Hardcover series reconcile: Hardcover is rate-limited ({Message}) -- stopping this pass",
                            ex.Message);
                        rateLimited = true;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Hardcover series reconcile: series {SeriesId} \"{Name}\" failed, left unchanged",
                            seriesItem.Id, seriesItem.Name);
                        // The failed save (if that was the cause) left its entities in the tracker;
                        // drop them so the next series and the final stamp save start clean.
                        db.ChangeTracker.Clear();
                        lastSynced[seriesItem.Id] = DateTime.UtcNow - StaleAfter + FailedRetryAfter;
                        if (++consecutiveFailures >= MaxConsecutiveFailures)
                        {
                            logger.LogWarning("Hardcover series reconcile: {Count} failures in a row -- stopping this pass", consecutiveFailures);
                            rateLimited = true; // reuse the stop flag: break out of both loops
                            break;
                        }
                    }
                }
                db.ChangeTracker.Clear();
                if (rateLimited) break;
            }
        }
        finally
        {
            // Entries for series that no longer carry a Hardcover series id (deleted, re-matched away)
            // are dropped so the setting can't grow without bound.
            var live = seriesIds.ToHashSet();
            await SaveLastSyncedAsync(db, lastSynced.Where(kv => live.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
        }

        logger.LogInformation(
            "Hardcover series reconcile: {Checked} of {Total} matched series checked ({Due} were due) -- {Ok} synced, {Bad} had a wrong/stale match removed{Stopped}",
            ok + badMatch, seriesIds.Count, due.Count, ok, badMatch, rateLimited ? ", stopped early" : "");
    }

    private static async Task<Dictionary<int, DateTime>> LoadLastSyncedAsync(ChronicleDbContext db, CancellationToken ct)
    {
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == LastSyncedSettingKey, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.Value)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<int, DateTime>>(row.Value) ?? []; }
        catch (JsonException) { return []; } // unreadable -> treat every series as due once
    }

    // Deliberately not cancellable: a cancelled/failed pass must still record what it DID sync.
    private static async Task SaveLastSyncedAsync(ChronicleDbContext db, Dictionary<int, DateTime> lastSynced)
    {
        db.ChangeTracker.Clear(); // a failed save earlier in the pass must not poison this one
        var json = JsonSerializer.Serialize(lastSynced);
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == LastSyncedSettingKey);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = LastSyncedSettingKey, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync();
    }
}
