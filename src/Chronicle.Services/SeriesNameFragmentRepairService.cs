using System.Text.RegularExpressions;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: a series item literally named "&lt;Base&gt; - N -
/// (Unknown)" or "&lt;Base&gt; - N - (YYYY)" is folded into the real "&lt;Base&gt;" series.
///
/// Root-caused live (2026-09-27): before "(Unknown)" was taught to ParseAudiobookFolderName as a missing
/// year (see FileScanService), a folder like "Undying Mercenaries - 13 - (Unknown) - Glass World" fell to
/// the parser's own no-year fallback, which joined every segment before the title into the series name --
/// producing a series ITEM named "Undying Mercenaries - 13 - (Unknown)" holding just that one book, sitting
/// beside the real 13-book "Undying Mercenaries" series. "Star Force - 11 - (Unknown)" (beside the real
/// 13-book "Star Force") is the same shape. The scanner now parses these correctly on a fresh scan; this
/// pass repairs what a scan already created: the base name before the position is exactly an existing
/// sibling series' name, which is what makes this safe to fold automatically rather than a guess -- a
/// series named merely "Star Force Universe" or "Star Force - Starship Pandora" (no trailing "- N - (...)"
/// this service recognises) is a different, ambiguous shape and is left alone.
/// </summary>
public sealed class SeriesNameFragmentRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<SeriesNameFragmentRepairService> logger)
{
    private static readonly Regex FragmentShape = new(
        @"^(?<base>.+?)\s*-\s*(?<num>\d{1,4}(?:\.\d+)?)\s*-\s*\((?:Unknown|\d{4})\)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Pure splitter, tested on its own: the base series name and the exact (possibly fractional)
    /// position this fragment shape names, or null when the name doesn't match it at all.</summary>
    internal static (string Base, double Position)? Split(string name)
    {
        var m = FragmentShape.Match(name.Trim());
        if (!m.Success) return null;
        var baseName = m.Groups["base"].Value.Trim();
        return double.TryParse(m.Groups["num"].Value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && baseName.Length > 0
            ? (baseName, n) : null;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var candidates = await db.MediaItems
            .Where(s => s.HierarchyLevel == 1 && s.ParentId != null && s.MediaType!.HierarchyLevels > 2)
            .ToListAsync(ct);

        var merged = 0;
        var reparented = 0;
        foreach (var fragment in candidates)
        {
            var split = Split(fragment.Name);
            if (split is not { } s) continue;

            var baseLower = s.Base.ToLowerInvariant();
            var series = await db.MediaItems.FirstOrDefaultAsync(x =>
                x.ParentId == fragment.ParentId && x.HierarchyLevel == 1 && x.Id != fragment.Id &&
                x.Name.ToLower() == baseLower, ct);
            if (series is null) continue; // no real sibling series -- nothing safe to fold into

            var books = await db.MediaItems.Where(b => b.ParentId == fragment.Id).ToListAsync(ct);
            foreach (var book in books)
            {
                // The book's own position wins over the fragment name's. Matched by the PRECISE position
                // (falling back to Number), so a fractional 1.1 novella is never mistaken for -- and merged
                // into -- book 1 just because both floor to Number 1.
                var position = SeriesPositionHelper.Effective(book) ?? s.Position;
                var existing = await db.MediaItems
                    .FirstOrDefaultAsync(b => b.ParentId == series.Id &&
                                              (b.SeriesPosition ?? (double?)b.Number) == position, ct);
                if (existing is not null && existing.Id != book.Id)
                {
                    logger.LogInformation(
                        "Series name fragment repair: \"{Book}\" ({BookId}) from fragment \"{Fragment}\" merged into " +
                        "\"{Existing}\" ({ExistingId}) in series \"{Series}\"",
                        book.Name, book.Id, fragment.Name, existing.Name, existing.Id, series.Name);
                    var mergeService = scope.ServiceProvider.GetRequiredService<IMergeService>();
                    await mergeService.MergeLoadedItemsAsync(db, existing, book, mergedByUserId: null, ct);
                    await db.SaveChangesAsync(ct);
                    merged++;
                }
                else
                {
                    book.ParentId = series.Id;
                    // Only fills an empty Number (with its matching SeriesPosition) -- one the book already
                    // has, and its own precise position, stay exactly as they were.
                    SeriesPositionHelper.FillIfEmpty(book, s.Position);
                    book.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    reparented++;
                }
            }
            db.MediaItems.Remove(fragment);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Series name fragment repair: fragment series \"{Fragment}\" ({FragmentId}) folded into \"{Series}\" ({SeriesId})",
                fragment.Name, fragment.Id, series.Name, series.Id);
        }

        logger.LogInformation(
            "Series name fragment repair: merged {Merged} book(s) into an existing one, moved {Reparented}, folding fragment series into their real series",
            merged, reparented);
    }
}
