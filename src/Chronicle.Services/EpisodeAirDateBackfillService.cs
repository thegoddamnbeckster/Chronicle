using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Scheduled task that re-queues episode enrichment so episodes pick up the air date the TMDB and
/// TVmaze plugins used to throw away.
///
/// Root-caused live (2026-09-26): every one of a Kodi device's 3,851 episodes had no air date,
/// because both plugins reduced an episode's air date to its YEAR and dropped the date itself, so
/// Chronicle's scraper API (which reads extendedData "air_date") had nothing to hand Kodi. The
/// plugins now keep the date, but every already-enriched episode still carries the old data and a
/// completed enrichment is never redone on its own.
///
/// Only TMDB and TVmaze rows for episodes whose stored partition has no air date, and only rows
/// completed BEFORE the fix shipped (<see cref="FixedAt"/>) -- an episode a provider genuinely has no
/// date for (an unannounced one) is re-fetched once and then left alone, not re-queued every night.
/// Bounded per run so the provider queue is never flooded.
/// </summary>
public sealed class EpisodeAirDateBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<EpisodeAirDateBackfillService> logger) : IScheduledTask
{
    public string TaskId      => "episode_air_date_backfill";
    public string DisplayName => "Episode Air Date Backfill";
    public string Description => "Re-fetches TV episodes enriched before air dates were kept, so every episode gets its air date.";
    public string DefaultCron => "0 4 * * *";

    /// <summary>When the plugins started keeping the air date; rows completed earlier are stale.</summary>
    internal static readonly DateTime FixedAt = new(2026, 9, 26, 3, 0, 0, DateTimeKind.Utc);

    internal const int MaxPerRun = 2000;

    private static readonly string[] PluginSuffixes = [".tmdb", ".tvmaze"];

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var queued = await RequeueAsync(db, ct);
        logger.LogInformation("Episode air date backfill: re-queued {Count} enrichment row(s) for episodes without an air date", queued);
    }

    internal static async Task<int> RequeueAsync(ChronicleDbContext db, CancellationToken ct)
    {
        var candidates = await db.MediaEnrichments
            .Where(e => e.Status == EnrichmentStatus.Completed &&
                        (e.LastCompletedAt == null || e.LastCompletedAt < FixedAt) &&
                        e.MediaItem!.HierarchyLevel == 2 &&
                        e.MediaItem.MediaType!.HierarchyLevels == 3 &&
                        e.MediaItem.MetadataJson != null &&
                        !e.MediaItem.MetadataJson.Contains("air_date"))
            .OrderBy(e => e.Id)
            .Take(MaxPerRun * 2)
            .ToListAsync(ct);

        var rows = candidates
            .Where(e => PluginSuffixes.Any(s => e.PluginId.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxPerRun)
            .ToList();

        foreach (var row in rows)
        {
            row.Status       = EnrichmentStatus.Pending;
            row.RetryCount   = 0;
            row.ErrorMessage = null;
        }
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
