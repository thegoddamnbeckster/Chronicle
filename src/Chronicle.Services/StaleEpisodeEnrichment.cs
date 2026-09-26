using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

/// <summary>
/// Re-queues, as part of a plugin's own "Fetch Missing Metadata" run, the episodes it enriched before
/// it started keeping an episode's air date.
///
/// Root-caused live (2026-09-26): every one of a Kodi device's 3,851 episodes had no air date,
/// because the TMDB and TVmaze plugins reduced an episode's air date to its YEAR and dropped the
/// date itself, so Chronicle's scraper API (which reads extendedData "air_date") had nothing to hand
/// Kodi. The plugins now keep the date, but an already-completed enrichment is never redone on its
/// own -- so the affected rows are put back to Pending here, and the same run that fetches every
/// other missing piece of metadata (EnrichPendingAsync) then fills them in. Deliberately NOT its own
/// scheduled task: all metadata refresh belongs in the one per-plugin pass.
///
/// Only rows completed BEFORE <see cref="FixedAt"/> are re-queued, so an episode a provider genuinely
/// has no date for (an unannounced one) is fetched once after the fix and then left alone. Bounded per
/// run so the provider queue is never flooded.
/// </summary>
internal static class StaleEpisodeEnrichment
{
    /// <summary>When the plugins started keeping the air date; rows completed earlier are stale.</summary>
    internal static readonly DateTime FixedAt = new(2026, 9, 26, 3, 0, 0, DateTimeKind.Utc);

    internal const int MaxPerRun = 2000;

    /// <summary>Plugins whose episode output gained the air date (plugin id suffix).</summary>
    private static readonly string[] AirDatePlugins = [".tmdb", ".tvmaze"];

    internal static async Task<int> RequeueAsync(ChronicleDbContext db, string pluginId, CancellationToken ct)
    {
        if (!AirDatePlugins.Any(s => pluginId.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            return 0;

        var rows = await db.MediaEnrichments
            .Where(e => e.PluginId == pluginId &&
                        e.Status == EnrichmentStatus.Completed &&
                        (e.LastCompletedAt == null || e.LastCompletedAt < FixedAt) &&
                        e.MediaItem!.HierarchyLevel == 2 &&
                        e.MediaItem.MediaType!.HierarchyLevels == 3 &&
                        e.MediaItem.MetadataJson != null &&
                        !e.MediaItem.MetadataJson.Contains("air_date"))
            .OrderBy(e => e.Id)
            .Take(MaxPerRun)
            .ToListAsync(ct);

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
