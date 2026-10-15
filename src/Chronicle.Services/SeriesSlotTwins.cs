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
/// at that position, and the real item carries no id from the provider (one that did is matched to a different
/// book). The placeholder's ids move to the real item, whose enrichment is queued again, and the placeholder is
/// deleted -- never one the user has data on.
/// </summary>
internal static class SeriesSlotTwins
{
    /// <summary>
    /// The one real item in <paramref name="series"/> at <paramref name="position"/> that the provider's list
    /// has not claimed, or null. Null too when the provider lists more than one book at that position.
    /// </summary>
    internal static MediaItem? FindTwin(
        IEnumerable<MediaItem> subtree, int seriesId, double position,
        IReadOnlyList<Chronicle.Plugins.Models.MediaMetadata> providerBooks, string providerSource,
        IReadOnlySet<int> manuallyPlacedIds)
    {
        var listedAtPosition = providerBooks.Count(p =>
            BookSeriesService.PositionOf(p) is { } pp && Math.Abs(pp - position) < 1e-9);
        if (listedAtPosition != 1) return null;

        var twins = subtree.Where(m =>
                m.ParentId == seriesId && m.HierarchyLevel == 2 && !m.IsStub &&
                !manuallyPlacedIds.Contains(m.Id) &&
                SeriesPositionHelper.Effective(m) is { } effective && Math.Abs(effective - position) < 1e-9 &&
                !m.ExternalIds.Any(e => string.Equals(e.Source, providerSource, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToList();
        return twins.Count == 1 ? twins[0] : null;
    }

    /// <summary>
    /// Gives <paramref name="twin"/> the ids of the placeholders (plus <paramref name="extraIds"/>), deletes the
    /// placeholders and queues the twin's enrichment again. Does nothing, and returns false, when a placeholder
    /// carries the user's own data (a library entry the user changed, play history, a list entry).
    /// </summary>
    internal static async Task<bool> AdoptAsync(
        ChronicleDbContext db, MediaItem twin, IReadOnlyCollection<MediaItem> placeholders,
        IEnumerable<(string Source, string ExternalId)> extraIds, ILogger logger, CancellationToken ct)
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

        foreach (var (source, externalId) in ids)
        {
            var heldElsewhere = await db.MediaExternalIds.AnyAsync(
                e => e.Source == source && e.ExternalId == externalId && e.MediaItemId != twin.Id, ct);
            if (heldElsewhere || twin.ExternalIds.Any(e => e.Source == source && e.ExternalId == externalId)) continue;
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = twin.Id, Source = source, ExternalId = externalId });
        }
        await BookSeriesService.ResetEnrichmentAsync(db, twin.Id, ct);
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
                var placeholders = slot.Where(b => b.IsStub && !manual.Contains(b.Id)).ToList();
                var real = slot.Where(b => !b.IsStub).ToList();
                if (placeholders.Count != 1 || real.Count != 1) continue;

                var placeholder = placeholders[0];
                var twin = real[0];
                var sources = placeholder.ExternalIds
                    .Select(e => e.Source)
                    .Where(s => !string.Equals(s, "chronicle", StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // A placeholder with no provider id is not a provider's book; a twin that already has an id
                // from one of the same providers is matched to a different book.
                if (sources.Count == 0 || manual.Contains(twin.Id) ||
                    twin.ExternalIds.Any(e => sources.Contains(e.Source))) continue;

                if (await SeriesSlotTwins.AdoptAsync(db, twin, [placeholder], [], logger, ct)) joined++;
            }
        }

        logger.LogInformation("Series slot twin repair: joined {Count} placeholder(s) to the book already in the library", joined);
    }
}
