using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: an item whose Year is impossible (65535, 0, -1 ...)
/// gets no year instead. See <see cref="ReleaseYear"/> for how such a year reaches Chronicle.
/// </summary>
public sealed class ImplausibleYearRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<ImplausibleYearRepairService> logger)
{
    /// <summary>app_settings key written once the stored plugin results have been cleaned of impossible years.</summary>
    public const string ScrubMarkerKey = "maintenance.plugin_year_scrub_v1";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var latest = ReleaseYear.Latest;
        // Rare rows -- loaded and saved through the change tracker (not a bulk update) so the item's
        // normalized-name bookkeeping and every other write hook still runs.
        var items = await db.MediaItems
            .Where(m => m.Year != null && (m.Year < ReleaseYear.Earliest || m.Year > latest))
            .ToListAsync(ct);
        foreach (var item in items) item.Year = null;
        await db.SaveChangesAsync(ct);
        var fixedCount = items.Count;

        // The years stored inside plugin results (a provider's own typo, e.g. 20222) too: they feed the item's resolved year.
        // A full pass reads every item's metadata (hundreds of MB), so it runs ONCE: from then on a plugin result is checked as
        // it is written (MetadataEnrichmentService.MergeMetadata), and the marker below records that the old data was cleaned.
        int scrubbed = 0, lastId = 0;
        var done = await db.AppSettings.AsNoTracking().AnyAsync(a => a.Key == ScrubMarkerKey, ct);
        while (!done)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await db.MediaItems.AsNoTracking()
                .Where(m => m.Id > lastId && m.MetadataJson != null)
                .OrderBy(m => m.Id).Take(2000)
                .Select(m => new { m.Id, m.MetadataJson })
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;
            foreach (var row in batch)
            {
                var clean = ReleaseYear.ScrubMetadataJson(row.MetadataJson);
                if (clean is null) continue;
                var item = await db.MediaItems.FindAsync([row.Id], ct);
                if (item is null) continue;
                item.MetadataJson = clean;
                scrubbed++;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        if (!done)
        {
            try
            {
                db.AppSettings.Add(new Chronicle.Core.Models.AppSetting { Key = ScrubMarkerKey, Value = DateTime.UtcNow.ToString("O") });
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // another run finished and recorded the marker first: nothing to do
            }
        }

        logger.LogInformation("Implausible year repair: cleared the year on {Count} item(s) and in the stored plugin data of {Scrubbed}", fixedCount, scrubbed);
    }
}
