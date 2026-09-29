using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: a standalone book whose title carries a series position
/// ("He Who Fights with Monsters #4") is folded into that series.
///
/// Root-caused live (2026-09-27): Hardcover's own book title for some series editions IS the base title
/// plus " #N" (no separate series field on the sync event), and SyncOrchestrationService.MatchOrCreateBookAsync
/// took that whole string as a standalone book title -- since no SeriesName was given, it never even looked
/// for the series. Every sync minted one such item per book, sitting beside the real, already-scanned
/// series (also a level-1 item under the same author) as if it were an unrelated title. The matcher now
/// splits the position out (like the audiobook file scanner's own series tag) and this repairs what was
/// already created: each fragment merges into the matching numbered book if the series already has one
/// (its external ids, library entries and credits move over via MergeService, nothing is lost), else the
/// fragment itself becomes that numbered book.
/// </summary>
public sealed class BookTitleFragmentRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<BookTitleFragmentRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        // Resolved from the SAME scope as db, so a merge below sees everything this pass has already
        // queried/changed -- MergeService is scoped, and a singleton repair service can't hold one directly
        // (it would outlive its own DbContext).
        var mergeService = scope.ServiceProvider.GetRequiredService<IMergeService>();

        // Standalone (level 1) items under an author whose type has a series level, whose title splits.
        var candidates = await db.MediaItems
            .Where(i => i.HierarchyLevel == 1 && i.ParentId != null && i.MediaType!.HierarchyLevels > 2)
            .ToListAsync(ct);

        var merged = 0;
        var reparented = 0;
        foreach (var stub in candidates)
        {
            // Precise: a "#1.5" title must reach SeriesPosition whole, not floored to 1.
            var (baseName, position) = FileScanService.SplitSeriesTagPrecise(stub.Name);
            if (!position.HasValue) continue;

            var baseNameLower = baseName.ToLowerInvariant();
            var series = await db.MediaItems.FirstOrDefaultAsync(s =>
                s.ParentId == stub.ParentId && s.HierarchyLevel == 1 && s.Id != stub.Id &&
                s.Name.ToLower() == baseNameLower, ct);
            if (series is null) continue; // no sibling series container -- leave it, nothing to fold into

            // Matched by the precise position (falling back to Number) so "#1.5" never merges into book 1.
            var scannedBook = await db.MediaItems
                .FirstOrDefaultAsync(b => b.ParentId == series.Id &&
                                          (b.SeriesPosition ?? (double?)b.Number) == position, ct);
            if (scannedBook is not null)
            {
                logger.LogInformation(
                    "Book title fragment repair: \"{Stub}\" ({StubId}) merged into \"{Book}\" ({BookId}) in series \"{Series}\"",
                    stub.Name, stub.Id, scannedBook.Name, scannedBook.Id, series.Name);
                // No transaction: this is the batch-loop usage IMergeService.MergeLoadedItemsAsync's own doc
                // describes (one SaveChangesAsync per pair), the same way DuplicateCleanupService's own
                // per-pair loop uses it -- MergeAsync's single-merge transaction isn't needed here.
                await mergeService.MergeLoadedItemsAsync(db, scannedBook, stub, mergedByUserId: null, ct);
                await db.SaveChangesAsync(ct);
                merged++;
            }
            else
            {
                stub.ParentId = series.Id;
                stub.HierarchyLevel = 2;
                // The title's position replaces whatever the stub had, so both fields are overwritten together.
                SeriesPositionHelper.Set(stub, position);
                stub.Name = baseName;
                stub.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                reparented++;
                logger.LogInformation(
                    "Book title fragment repair: \"{Stub}\" ({StubId}) moved into series \"{Series}\" ({SeriesId}) as book {Position}",
                    stub.Name, stub.Id, series.Name, series.Id, position);
            }
        }

        logger.LogInformation(
            "Book title fragment repair: merged {Merged} fragment(s) into an existing book, moved {Reparented} into their series",
            merged, reparented);
    }
}
