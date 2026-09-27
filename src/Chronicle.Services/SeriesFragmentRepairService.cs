using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: series items whose name carries the book's position
/// ("Laundry Files #5", "Backyard Starship, Book #29") are folded back into the one real series.
///
/// Root-caused live (2026-09-27): an audiobook's series tag often reads "Series #N", and the scanner took
/// that whole string as the series name, so every such book got its OWN one-book series and the real
/// series was split across dozens of items (54 of them, none in reading order). The scanner now splits the
/// tag; this pass repairs what was already imported: the books move into the author's series with the
/// bare name (created by renaming the first fragment when there is none), each takes its position from the
/// fragment's "#N" when it has no Number, and the emptied fragment items are removed.
/// </summary>
public sealed class SeriesFragmentRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<SeriesFragmentRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Every series (level 1 with children) of a type that has a series level.
        var series = await db.MediaItems
            .Where(s => s.HierarchyLevel == 1 && s.ParentId != null && s.MediaType!.HierarchyLevels > 2 &&
                        db.MediaItems.Any(k => k.ParentId == s.Id))
            .ToListAsync(ct);

        var groups = series
            .Select(s => (Item: s, Split: FileScanService.SplitSeriesTag(s.Name)))
            .GroupBy(x => (x.Item.ParentId, x.Item.MediaTypeId, Key: BaseKey(x.Split.Name)))
            .Where(g => g.Any(x => x.Split.Number.HasValue))   // at least one "#N" fragment in the group
            .ToList();

        var moved = 0;
        var removed = 0;
        foreach (var g in groups)
        {
            var fragments = g.Where(x => x.Split.Number.HasValue).ToList();
            var whole = g.Where(x => !x.Split.Number.HasValue).Select(x => x.Item)
                .OrderByDescending(s => db.MediaItems.Count(k => k.ParentId == s.Id)).FirstOrDefault();

            // No series with the bare name yet: the first fragment becomes it (renamed below).
            var canonical = whole ?? fragments[0].Item;

            foreach (var (fragment, split) in fragments)
            {
                if (fragment.Id == canonical.Id)
                {
                    // The renamed fragment itself: give its own books their position, then drop the suffix.
                    foreach (var b in await db.MediaItems.Where(k => k.ParentId == fragment.Id && k.Number == null).ToListAsync(ct))
                        b.Number = split.Number;
                    fragment.Name = split.Name;
                    fragment.UpdatedAt = DateTime.UtcNow;
                    continue;
                }

                foreach (var book in await db.MediaItems.Where(k => k.ParentId == fragment.Id).ToListAsync(ct))
                {
                    book.ParentId = canonical.Id;
                    book.Number ??= split.Number;
                    book.UpdatedAt = DateTime.UtcNow;
                    moved++;
                }
                await db.SaveChangesAsync(ct);
                db.MediaItems.Remove(fragment);
                removed++;
                logger.LogInformation(
                    "Series fragment repair: series \"{Fragment}\" ({FragmentId}) folded into \"{Series}\" ({SeriesId})",
                    fragment.Name, fragment.Id, canonical.Name, canonical.Id);
            }
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Series fragment repair: moved {Moved} book(s), removed {Removed} fragment series", moved, removed);
    }

    /// <summary>Series names compare ignoring case, punctuation and a leading "The".</summary>
    internal static string BaseKey(string name)
    {
        var n = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return n.StartsWith("the", StringComparison.Ordinal) && n.Length > 3 ? n[3..] : n;
    }
}
