using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One-time recompute of <c>media_items.normalized_name_loose</c> after
/// <see cref="MediaItemNormalizer.NormalizeNameLoose"/> began folding diacritics (2026-09-15,
/// commit 46d3f0d). The column is a stored, indexed key: ChronicleDbContext keeps it current for
/// every write from then on, but rows nobody happened to re-save still carry the old, accented
/// key -- so "Ismael Cruz Córdova" (stored "ismaelcruzcórdova") never matched a credit for
/// "Ismael Cruz Cordova" (computed "ismaelcruzcordova"), and a second person record was created.
///
/// Only the key is rewritten (ExecuteUpdate, not a tracked save): the save hook would also bump
/// <c>UpdatedAt</c>, which the Kodi addons read as "this item changed, re-scrape it", and nothing
/// the user can see has changed. The item's <c>Name</c> is never touched here -- which spelling is
/// displayed stays the job of metadata assignment precedence (MetadataResolutionService promotes
/// the winning provider's title), including after the duplicates this exposes get merged.
///
/// Gated by an app_settings marker so it runs exactly once; the duplicate scan and person
/// auto-resolve (DuplicateMaintenanceTask) then pick up the newly matching pairs.
/// </summary>
public sealed class NormalizedNameLooseBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<NormalizedNameLooseBackfillService> logger)
{
    internal const string MarkerKey = "maintenance.normalized_name_loose_diacritics";
    private const int BatchSize = 5000;

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        if (await db.AppSettings.AnyAsync(s => s.Key == MarkerKey, ct)) return;

        int lastId = 0, scanned = 0, updated = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await db.MediaItems.AsNoTracking()
                .Where(m => m.Id > lastId)
                .OrderBy(m => m.Id)
                .Take(BatchSize)
                .Select(m => new { m.Id, m.Name, m.NormalizedNameLoose })
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var row in batch)
            {
                var correct = MediaItemNormalizer.NormalizeNameLoose(row.Name);
                if (correct == (row.NormalizedNameLoose ?? string.Empty)) continue;

                var id = row.Id;
                await db.MediaItems.Where(m => m.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.NormalizedNameLoose, correct), ct);
                updated++;
            }

            scanned += batch.Count;
            lastId = batch[^1].Id;
        }

        db.AppSettings.Add(new AppSetting { Key = MarkerKey, Value = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Normalized-name-loose backfill: {Scanned} item(s) checked, {Updated} key(s) recomputed",
            scanned, updated);
    }
}
