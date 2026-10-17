using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// A book of the user's own (a file, no provider id) and a provider's placeholder for the same book sitting at
/// the same position in the same series: "Heroes" #12 beside Hardcover's "Hell Divers XII: Heroes" #12. A
/// provider's book is matched to an item by id, then by title and year; when the user's book was never matched
/// and its title differs, neither works, so the placeholder was created beside it and the library showed a
/// book with a file and no metadata next to a book with metadata and no file.
///
/// The position settles it, but only when it is unambiguous: exactly one placeholder and exactly one real item
/// at that position. A real item with no id from the provider simply takes the placeholder's. One that already
/// carries a provider id (a different edition of the same book: "Dungeon Crawler Carl: A LitRPG/Gamelit
/// Adventure" against Hardcover's "Dungeon Crawler Carl", both #1) keeps it and is only joined when the two
/// titles are related -- one contains the other, or the book's own folder name contains the placeholder's title --
/// since otherwise it may be matched to a genuinely different book. When only the folder name connects them (the
/// book's title and id came from a wrong match: the file "Dungeon Crawler Carl - 5 - (2022) - The Butcher's
/// Masquerade" that carried the id of "The Dungeon Anarchist's Cookbook, Book 3"), the wrong id is replaced by the
/// placeholder's. The placeholder is deleted, never one the user has data on.
/// </summary>
internal static class SeriesSlotTwins
{
    /// <summary>A provider's placeholder: flagged as one AND holding no file. The flag alone is not enough --
    /// it was left set when the scanner attached a file, so a flagged item can be the user's own book.</summary>
    internal static bool IsPlaceholder(MediaItem m) =>
        m.IsStub && !Chronicle.Services.Scan.FileIdentityJson.HasKnownFile(m.MetadataJson);

    /// <summary>
    /// The one real item in <paramref name="series"/> at <paramref name="position"/> that the provider's list
    /// has not claimed, or null. Null too when the provider lists more than one book at that position.
    /// </summary>
    internal static MediaItem? FindTwin(
        IEnumerable<MediaItem> subtree, int seriesId, double position,
        IReadOnlyList<Chronicle.Plugins.Models.MediaMetadata> providerBooks, string providerSource,
        IReadOnlySet<int> manuallyPlacedIds, ISet<string> listedIds, string providerTitle)
    {
        var listedAtPosition = providerBooks.Count(p =>
            BookSeriesService.PositionOf(p) is { } pp && Math.Abs(pp - position) < 1e-9);
        if (listedAtPosition != 1) return null;

        var twins = subtree.Where(m =>
                m.ParentId == seriesId && m.HierarchyLevel == 2 && !IsPlaceholder(m) &&
                !manuallyPlacedIds.Contains(m.Id) &&
                SeriesPositionHelper.Effective(m) is { } effective && Math.Abs(effective - position) < 1e-9 &&
                // never one the provider's list already claims for another book
                !m.ExternalIds.Any(e => listedIds.Contains(e.ExternalId)) &&
                // one that already has a provider id of its own must also read as the same book
                (!m.ExternalIds.Any(e => string.Equals(e.Source, providerSource, StringComparison.OrdinalIgnoreCase)) ||
                 NamesRelated(m.Name, providerTitle) || FolderRelated(m, providerTitle)))
            .Take(2)
            .ToList();
        return twins.Count == 1 ? twins[0] : null;
    }

    /// <summary>The name of the folder the scanner found the book in contains the provider's title (normalised).</summary>
    internal static bool FolderRelated(MediaItem m, string? providerTitle)
    {
        var path = MisparentedChildRepairService.FolderOf(m.MetadataJson);
        var folder = path is null ? null : Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar).Replace((char)92, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar));
        var nf = Chronicle.Core.Helpers.MediaItemNormalizer.NormalizeName(folder ?? "");
        var nt = Chronicle.Core.Helpers.MediaItemNormalizer.NormalizeName(providerTitle ?? "");
        return nt.Length >= 3 && nf.Length >= 3 && nf.Contains(nt, StringComparison.Ordinal);
    }

    /// <summary>Both hold the same id from the same provider: one book, whatever the two spell the title as
    /// ("Enemy A[n]t the Gates" beside "Enemy A(n)t the Gates", which normalise differently).</summary>
    internal static bool SharesProviderId(MediaItem a, MediaItem b) =>
        a.ExternalIds.Any(x => !string.Equals(x.Source, "chronicle", StringComparison.OrdinalIgnoreCase) &&
                               x.ExternalId != BookSeriesService.ManualSeriesMemberMarker &&
                               b.ExternalIds.Any(y => string.Equals(x.Source, y.Source, StringComparison.OrdinalIgnoreCase) &&
                                                      string.Equals(x.ExternalId, y.ExternalId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>One title contains the other once both are normalised ("Dungeon Crawler Carl" in "Dungeon Crawler Carl: A LitRPG ...").</summary>
    internal static bool NamesRelated(string? a, string? b)
    {
        var na = Chronicle.Core.Helpers.MediaItemNormalizer.NormalizeName(a ?? "");
        var nb = Chronicle.Core.Helpers.MediaItemNormalizer.NormalizeName(b ?? "");
        if (na.Length < 3 || nb.Length < 3) return false;
        return na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gives <paramref name="twin"/> the ids of the placeholders (plus <paramref name="extraIds"/>), deletes the
    /// placeholders and queues the twin's enrichment again. Does nothing, and returns false, when a placeholder
    /// carries the user's own data (a library entry the user changed, play history, a list entry).
    /// </summary>
    internal static async Task<bool> AdoptAsync(
        ChronicleDbContext db, MediaItem twin, IReadOnlyCollection<MediaItem> placeholders,
        IEnumerable<(string Source, string ExternalId)> extraIds, ILogger logger, CancellationToken ct,
        string? providerTitle = null, bool allowReplacingOwnIds = true)
    {
        var protectedIds = await BookSeriesService.StubIdsWithUserDataAsync(db, placeholders.Select(p => p.Id), ct);
        if (placeholders.Any(p => protectedIds.Contains(p.Id))) return false;

        var ids = placeholders.SelectMany(p => p.ExternalIds.Select(e => (e.Source, e.ExternalId)))
            .Concat(extraIds)
            .Where(i => !string.Equals(i.Source, "chronicle", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        // An external id belongs to one item, so the placeholders' go first.
        foreach (var placeholder in placeholders)
        {
            db.MediaExternalIds.RemoveRange(placeholder.ExternalIds);
            db.MediaEnrichments.RemoveRange(db.MediaEnrichments.Where(e => e.MediaItemId == placeholder.Id));
            db.MediaItems.Remove(placeholder);
        }
        await db.SaveChangesAsync(ct);

        // Only the folder name ties them (the title does not): the id the book carries came from a wrong match.
        var title = providerTitle ?? placeholders.FirstOrDefault()?.Name;
        var replaceOwnIds = allowReplacingOwnIds && !NamesRelated(twin.Name, title) && FolderRelated(twin, title);

        var added = false;
        foreach (var (source, externalId) in ids)
        {
            // An id may be shared with the same work in another format (a book and its audiobook carry the same
            // Hardcover id); only another item of the SAME type holding it is a conflict. Checked before anything
            // is removed, so a blocked id never costs the twin the one it already has.
            var heldElsewhere = await db.MediaExternalIds.AnyAsync(
                e => e.Source == source && e.ExternalId == externalId && e.MediaItemId != twin.Id &&
                     e.MediaItem!.MediaTypeId == twin.MediaTypeId, ct);
            if (heldElsewhere) continue;

            var own = twin.ExternalIds.Where(e => string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase)).ToList();
            if (own.Any(e => e.ExternalId == externalId)) continue;
            if (own.Count > 0 && replaceOwnIds)
            {
                db.MediaExternalIds.RemoveRange(own);
                foreach (var gone in own) twin.ExternalIds.Remove(gone);
                await db.SaveChangesAsync(ct);
                own.Clear();
            }
            // one id per source: a twin that already has one of its own keeps it
            if (own.Count > 0) continue;
            var gained = new MediaExternalId { MediaItemId = twin.Id, Source = source, ExternalId = externalId };
            db.MediaExternalIds.Add(gained);
            twin.ExternalIds.Add(gained);
            added = true;
        }
        // Only when it gained an id: providers need to look again. Otherwise what it already has stands.
        if (added) await BookSeriesService.ResetEnrichmentAsync(db, twin.Id, ct);
        twin.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Book {TwinId} \"{TwinName}\" is the same book as the provider's placeholder(s) {Placeholders} at its series position -- " +
            "ids moved onto it, placeholder removed, enrichment queued again",
            twin.Id, twin.Name, string.Join(", ", placeholders.Select(p => $"{p.Id} \"{p.Name}\"")));
        return true;
    }
}

/// <summary>
/// One pass of the Library Integrity Repair task: finds the pairs described on <see cref="SeriesSlotTwins"/>
/// already in the library and joins them. Needs no provider call, so it works while a provider is out of
/// quota. Only an unambiguous pair is touched (one placeholder, one real item, nothing else at that position).
/// </summary>
public sealed class SeriesSlotTwinRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<SeriesSlotTwinRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var books = await db.MediaItems
            .Include(m => m.ExternalIds)
            .Where(m => m.HierarchyLevel == 2 && m.ParentId != null && m.MediaType!.HierarchyLevels > 2)
            .ToListAsync(ct);
        var manual = books
            .Where(b => b.ExternalIds.Any(e => e.ExternalId == BookSeriesService.ManualSeriesMemberMarker))
            .Select(b => b.Id)
            .ToHashSet();

        var joined = 0;
        foreach (var seriesBooks in books.GroupBy(b => b.ParentId!.Value))
        {
            foreach (var slot in seriesBooks.Where(b => SeriesPositionHelper.Effective(b).HasValue)
                         .GroupBy(b => SeriesPositionHelper.Effective(b)!.Value))
            {
                var placeholders = slot.Where(b => SeriesSlotTwins.IsPlaceholder(b) && !manual.Contains(b.Id)).ToList();
                var real = slot.Where(b => !SeriesSlotTwins.IsPlaceholder(b)).ToList();
                if (placeholders.Count != 1 || real.Count != 1) continue;

                var placeholder = placeholders[0];
                var twin = real[0];
                var sources = placeholder.ExternalIds
                    .Select(e => e.Source)
                    .Where(s => !string.Equals(s, "chronicle", StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // A placeholder with no provider id is not a provider's book; a twin that already has an id
                // from one of the same providers is matched to a different book.
                if (sources.Count == 0 || manual.Contains(twin.Id)) continue;
                // A twin with an id of the same provider of its own is only joined when the titles are related.
                if (twin.ExternalIds.Any(e => sources.Contains(e.Source)) &&
                    !SeriesSlotTwins.NamesRelated(twin.Name, placeholder.Name) &&
                    !SeriesSlotTwins.FolderRelated(twin, placeholder.Name)) continue;

                if (await SeriesSlotTwins.AdoptAsync(db, twin, [placeholder], [], logger, ct)) joined++;
            }
        }

        logger.LogInformation("Series slot twin repair: joined {Count} placeholder(s) to the book already in the library", joined);
        await RemoveFilelessDuplicatesAsync(db, ct);
    }

    /// <summary>
    /// The item that holds the user's file is THE entry. When another item sits at the same position in the same
    /// series, holds no file, belongs to nobody (no rating, status, notes or play history) and reads as the same
    /// book, it is a leftover duplicate and is removed; the entry with the file keeps its own ids and takes any it
    /// lacked. Reading as the same book means the titles are related, the folder name holds the title, or the two hold the
/// same provider id. Two items that BOTH hold a file are never touched (two editions on disk), nor is anything ambiguous.
    /// </summary>
    private async Task RemoveFilelessDuplicatesAsync(ChronicleDbContext db, CancellationToken ct)
    {
        var books = await db.MediaItems
            .Include(m => m.ExternalIds)
            .Where(m => m.HierarchyLevel == 2 && m.ParentId != null && m.MediaType!.HierarchyLevels > 2)
            .ToListAsync(ct);
        var manual = books
            .Where(b => b.ExternalIds.Any(e => e.ExternalId == BookSeriesService.ManualSeriesMemberMarker))
            .Select(b => b.Id)
            .ToHashSet();

        var removed = 0;
        foreach (var seriesBooks in books.GroupBy(b => b.ParentId!.Value))
        {
            foreach (var slot in seriesBooks.Where(b => SeriesPositionHelper.Effective(b).HasValue)
                         .GroupBy(b => SeriesPositionHelper.Effective(b)!.Value))
            {
                var holders = slot.Where(b => Chronicle.Services.Scan.FileIdentityJson.HasKnownFile(b.MetadataJson)).ToList();
                if (holders.Count != 1) continue;                       // none, or two editions on disk
                var keeper = holders[0];

                var leftovers = slot
                    .Where(b => b.Id != keeper.Id && !manual.Contains(b.Id) &&
                                !Chronicle.Services.Scan.FileIdentityJson.HasKnownFile(b.MetadataJson) &&
                                (SeriesSlotTwins.NamesRelated(b.Name, keeper.Name) || SeriesSlotTwins.FolderRelated(keeper, b.Name) ||
                                 SeriesSlotTwins.SharesProviderId(b, keeper)))
                    .ToList();
                if (leftovers.Count == 0 || manual.Contains(keeper.Id)) continue;

                if (await SeriesSlotTwins.AdoptAsync(db, keeper, leftovers, [], logger, ct, allowReplacingOwnIds: false)) removed += leftovers.Count;
            }
        }

        logger.LogInformation("Series slot twin repair: removed {Count} file-less duplicate(s) of a book that holds a file", removed);
    }
}
