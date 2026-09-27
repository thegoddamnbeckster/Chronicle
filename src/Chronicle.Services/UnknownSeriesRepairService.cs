using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: a series literally named "(Unknown)" is dissolved.
///
/// Root-caused live (2026-09-27): a book folder named "- - (Unknown) - Title" has no year ("(Unknown)"
/// stands where the year would be), which the folder parser did not recognise, so it read "(Unknown)" as a
/// SERIES name. Books from different authors ended up in one shared "(Unknown)" series, several under
/// an author they do not belong to (Charles Stross's, Jeremy Robinson's and Matt Dinniman's books under
/// "pirateaba"). The parser now treats "(Unknown)" as a missing year; this pass repairs what was imported:
/// each such book becomes a standalone book (level 1) directly under the author whose folder contains its
/// own folder -- falling back to the series' author when no author folder matches -- and the emptied
/// "(Unknown)" series is removed.
/// </summary>
public sealed class UnknownSeriesRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<UnknownSeriesRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var seriesList = await db.MediaItems
            .Where(s => s.HierarchyLevel == 1 && s.ParentId != null && s.MediaType!.HierarchyLevels > 2 &&
                        s.Name.ToLower() == "(unknown)")
            .ToListAsync(ct);
        if (seriesList.Count == 0)
        {
            logger.LogInformation("Unknown series repair: nothing to repair");
            return;
        }

        var typeIds = seriesList.Select(s => s.MediaTypeId).Distinct().ToList();
        var authors = (await db.MediaItems
                .Where(a => a.HierarchyLevel == 0 && typeIds.Contains(a.MediaTypeId))
                .Select(a => new { a.Id, a.MediaTypeId, a.MetadataJson })
                .ToListAsync(ct))
            .Select(a => (a.Id, a.MediaTypeId, Folder: MisparentedChildRepairService.NormalizeFolder(
                MisparentedChildRepairService.FolderOf(a.MetadataJson))))
            .Where(a => a.Folder is not null)
            .ToList();

        var moved = 0;
        var removed = 0;
        foreach (var series in seriesList)
        {
            var books = await db.MediaItems.Where(k => k.ParentId == series.Id).ToListAsync(ct);
            foreach (var book in books)
            {
                var authorFolder = MisparentedChildRepairService.NormalizeFolder(
                    MisparentedChildRepairService.ParentFolder(MisparentedChildRepairService.FolderOf(book.MetadataJson)));
                var owner = authorFolder is null ? default
                    : authors.FirstOrDefault(a => a.MediaTypeId == book.MediaTypeId && a.Folder == authorFolder);

                book.ParentId = owner == default ? series.ParentId : owner.Id;
                book.HierarchyLevel = 1;
                book.UpdatedAt = DateTime.UtcNow;
                moved++;
                logger.LogInformation(
                    "Unknown series repair: \"{Book}\" ({BookId}) moved out of the \"(Unknown)\" series ({SeriesId}) to author {AuthorId}",
                    book.Name, book.Id, series.Id, book.ParentId);
            }
            await db.SaveChangesAsync(ct);
            db.MediaItems.Remove(series);
            removed++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Unknown series repair: moved {Moved} book(s), removed {Removed} \"(Unknown)\" series", moved, removed);
    }
}
