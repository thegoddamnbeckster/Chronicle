using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: a book in a series that has no Number gets its series
/// position from its own folder name ("Singularity - 2 - (2012) - A.I. Apocalypse" is book 2), so a series
/// lists in reading order instead of alphabetically.
///
/// The scanner now records that position when it imports a folder, but it only revisits a folder on a
/// scan; this pass heals every existing book from the folder path it already stored, and keeps healing
/// any book that arrives without one. It only fills an EMPTY Number, so a position set by a provider or
/// by hand is never overwritten.
/// </summary>
public sealed class AudiobookSeriesNumberRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<AudiobookSeriesNumberRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Books inside a series (level 2 of a type with a series level) that have no position yet.
        var candidates = await db.MediaItems
            .Where(m => m.HierarchyLevel == 2 && m.Number == null && m.MediaType!.HierarchyLevels > 2 &&
                        m.MetadataJson != null && m.MetadataJson.Contains("fileScanner"))
            .ToListAsync(ct);

        var fixedCount = 0;
        foreach (var book in candidates)
        {
            var folder = MisparentedChildRepairService.FolderOf(book.MetadataJson);
            var name = folder is null ? null : Path.GetFileName(folder.Replace('\\', '/').TrimEnd('/'));
            if (string.IsNullOrEmpty(name)) continue;

            // The precise parse: a "1.1" folder position must reach SeriesPosition whole, not floored.
            var (_, _, series, position) = FileScanService.ParseAudiobookFolderNamePrecise(name);
            if (series is null || position is null) continue;

            if (!SeriesPositionHelper.FillIfEmpty(book, position)) continue;
            book.UpdatedAt = DateTime.UtcNow;
            fixedCount++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Audiobook series number repair: set the series position on {Count} book(s)", fixedCount);
    }
}
