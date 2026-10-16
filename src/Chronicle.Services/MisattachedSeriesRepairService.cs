using System.Text.Json;
using System.Text.Json.Nodes;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: a standalone book that was given another author's SERIES is turned
/// back into a book.
///
/// In an Author -> Series -> Book type a standalone book sits at the series level too, so a provider that finds a
/// series of the same name ("Prey" by Carmen Rosales for Michael Crichton's "Prey", "Next" by Scott Nicholson for his
/// "Next") can attach it to the book. The series sync then built that series' books underneath it as placeholders,
/// and the book showed their covers as its own. 28 books were in this state.
///
/// Only an unmistakable case is touched: a level-1 item that itself holds a file (a series container holds none; its
/// books do), whose children are ALL placeholders (no file, nothing the user has done). Its series ids are removed, the
/// placeholders go, the series' stored metadata is dropped, and its enrichment is queued again as a book. A book that
/// has real children is left alone.
/// </summary>
public sealed class MisattachedSeriesRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<MisattachedSeriesRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var resolution = scope.ServiceProvider.GetRequiredService<IMetadataResolutionService>();

        var candidates = await db.MediaItems
            .Where(m => m.HierarchyLevel == 1 && m.ParentId != null && m.MediaType!.HierarchyLevels > 2 &&
                        m.MetadataJson != null && m.MetadataJson.Contains("fileScanner") &&
                        db.MediaItems.Any(c => c.ParentId == m.Id) &&
                        db.MediaExternalIds.Any(e => e.MediaItemId == m.Id && e.ExternalId.Contains("series:")))
            .Select(m => m.Id)
            .ToListAsync(ct);

        int repaired = 0, placeholders = 0;
        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var removed = await RepairAsync(db, resolution, id, ct);
                if (removed >= 0) { repaired++; placeholders += removed; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(ex, "Misattached series repair: item {Id} failed, left unchanged", id);
            }
            db.ChangeTracker.Clear();
        }

        logger.LogInformation(
            "Misattached series repair: turned {Repaired} book(s) back into books, removed {Placeholders} placeholder(s)",
            repaired, placeholders);
    }

    /// <summary>Returns how many placeholders were removed, or -1 when the item is not an unmistakable case.</summary>
    private async Task<int> RepairAsync(ChronicleDbContext db, IMetadataResolutionService resolution, int id, CancellationToken ct)
    {
        var book = await db.MediaItems.Include(m => m.ExternalIds).FirstAsync(m => m.Id == id, ct);
        if (!FileIdentityJson.HasKnownFile(book.MetadataJson)) return -1;       // a series container holds no file itself

        var children = await db.MediaItems.Include(m => m.ExternalIds).Where(m => m.ParentId == id).ToListAsync(ct);
        if (children.Count == 0 || children.Any(c => !SeriesSlotTwins.IsPlaceholder(c))) return -1;
        var protectedIds = await BookSeriesService.StubIdsWithUserDataAsync(db, children.Select(c => c.Id), ct);
        if (protectedIds.Count > 0) return -1;

        var seriesIds = book.ExternalIds
            .Where(e => e.ExternalId.Contains("series:", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(e.Source, "chronicle", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (seriesIds.Count == 0) return -1;

        foreach (var child in children)
        {
            db.MediaExternalIds.RemoveRange(child.ExternalIds);
            db.MediaEnrichments.RemoveRange(db.MediaEnrichments.Where(e => e.MediaItemId == child.Id));
            db.MediaItems.Remove(child);
        }
        db.MediaExternalIds.RemoveRange(seriesIds);

        // What the wrong series stored on the book (its title, cover, overview) goes with it; the book's own
        // lookup refills it. Partitions are keyed by the short source or the full plugin id.
        var sources = seriesIds.Select(e => e.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (JsonNode.Parse(book.MetadataJson ?? "{}") is JsonObject root)
        {
            foreach (var key in root.Select(kv => kv.Key).ToList())
                if (sources.Any(s => string.Equals(key, s, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(key, "chronicle.plugin." + s, StringComparison.OrdinalIgnoreCase)))
                    root.Remove(key);
            book.MetadataJson = root.ToJsonString();
        }
        book.PosterUrl = null;
        book.UpdatedAt = DateTime.UtcNow;

        foreach (var row in await db.MediaEnrichments.Where(e => e.MediaItemId == id).ToListAsync(ct))
        {
            row.Status = EnrichmentStatus.Pending;
            row.RetryCount = 0;
            row.LastAttemptedAt = null;
            row.ErrorMessage = null;
        }
        await db.SaveChangesAsync(ct);
        await resolution.ResolveAsync(book, db, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Misattached series repair: \"{Name}\" ({Id}) is a book, not the series {Series}; removed {Count} placeholder(s) and queued it again",
            book.Name, id, string.Join(", ", seriesIds.Select(e => e.ExternalId)), children.Count);
        return children.Count;
    }
}
