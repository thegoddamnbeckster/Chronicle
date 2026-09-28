using System.Text.Json;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Author -&gt; Series -&gt; Book counterpart of <see cref="MovieCollectionService"/>'s collection-stub
/// machinery -- see <see cref="IBookSeriesService"/> for the design and why it exists.
/// </summary>
public class BookSeriesService(
    IServiceScopeFactory scopeFactory,
    ILogger<BookSeriesService> logger) : IBookSeriesService
{
    /// <summary>
    /// Sentinel MediaExternalId.ExternalId (Source "chronicle") marking that a book's current
    /// series membership (or lack of one) was explicitly chosen by the user via
    /// ReparentIntoSeriesAsync, not inferred from a provider's book list. See
    /// EnsureSeriesStubsAsync, which skips any book carrying this marker entirely.
    /// </summary>
    private const string ManualSeriesMemberMarker = "manual-series-member";

    public async Task<bool> EnsureSeriesStubsAsync(
        ChronicleDbContext db,
        MediaItem series,
        IMetadataProvider provider,
        CancellationToken ct = default,
        IReadOnlyList<(string PluginId, IMetadataProvider Provider)>? allProviders = null)
    {
        if (series.MediaType is null)
            await db.Entry(series).Reference(m => m.MediaType).LoadAsync(ct);
        if (series.MediaType is null || series.MediaType.HierarchyLevels < 3 || series.ParentId is null)
            return true; // not an Author -> Series -> Book type, or has no author -- nothing to do

        var callerEntry = allProviders?.FirstOrDefault(p => ReferenceEquals(p.Provider, provider));
        var providerSource = callerEntry.HasValue
            ? PluginIdHelper.ToSource(callerEntry.Value.PluginId)
            : PluginIdHelper.ToSource(provider.Name);
        var pluginId = callerEntry.HasValue ? callerEntry.Value.PluginId : provider.Name;

        var seriesExtId = await db.MediaExternalIds.FirstOrDefaultAsync(e =>
            e.MediaItemId == series.Id && e.Source == providerSource &&
            e.ExternalId.Contains("series:"), ct);
        if (seriesExtId is null) return true; // this provider doesn't know this item as a series

        MediaMetadata? seriesMeta;
        try
        {
            seriesMeta = await provider.GetByIdAsync(seriesExtId.ExternalId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to fetch series book list for {ExternalId} from provider {Provider} -- removing bad ExternalId to prevent repeat failures",
                seriesExtId.ExternalId, provider.Name);
            db.MediaExternalIds.Remove(seriesExtId);
            await db.SaveChangesAsync(ct);
            return true;
        }

        // Same wrong-match guard as EnsureCollectionStubsAsync: the provider's own name for this
        // id must match the container we already have, or this is a stale/wrong ExternalId.
        if (!string.IsNullOrEmpty(seriesMeta.Title) &&
            !string.Equals(seriesMeta.Title, series.Name, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Series {SeriesId} \"{ContainerName}\": provider returned name '{ProviderName}' -- " +
                "ExternalId {ExternalId} is a wrong match. Removing it.",
                series.Id, series.Name, seriesMeta.Title, seriesExtId.ExternalId);
            db.MediaExternalIds.Remove(seriesExtId);
            await db.SaveChangesAsync(ct);
            return false;
        }

        await PersistSeriesMetadataAsync(db, series, seriesMeta, pluginId, ct);

        // Deliberately NOT an early return when Results is empty -- an empty list is still
        // authoritative (the series has no books at all any more) and must still clear out any
        // now-stale stubs below, not just skip straight past that step.
        var results = seriesMeta.Results ?? [];

        var authorId = series.ParentId.Value;
        // Every item under this author's whole subtree, at ANY level -- a standalone book
        // (level 1, same as this series container) or a book already inside some OTHER series
        // (level 2) -- so a book that moved between "standalone" and "in this series" on the
        // provider's end is found and re-leveled instead of duplicated. See this type's own doc.
        var authorSeriesIds = await db.MediaItems
            .Where(m => m.ParentId == authorId && m.HierarchyLevel == 1)
            .Select(m => m.Id)
            .ToListAsync(ct);
        var subtree = await db.MediaItems
            .Include(m => m.ExternalIds)
            .Where(m => m.ParentId == authorId || (m.ParentId != null && authorSeriesIds.Contains(m.ParentId.Value)))
            .ToListAsync(ct);
        var manuallyPlacedIds = subtree
            .Where(m => m.ExternalIds.Any(e => e.ExternalId == ManualSeriesMemberMarker))
            .Select(m => m.Id)
            .ToHashSet();

        var authoritative = results
            .Where(p => !string.IsNullOrEmpty(p.ExternalId))
            .Select(p => p.ExternalId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Remove stale stubs: a stub of THIS series no longer in the provider's list, and never
        // manually placed (a manually-added stub can't happen via this path, but a defensive
        // check costs nothing and future-proofs against one).
        var staleStubs = subtree.Where(m => m.ParentId == series.Id && m.IsStub &&
            !manuallyPlacedIds.Contains(m.Id) &&
            !m.ExternalIds.Any(e => authoritative.Contains(e.ExternalId))).ToList();
        foreach (var stale in staleStubs)
        {
            db.MediaExternalIds.RemoveRange(stale.ExternalIds);
            db.MediaItems.Remove(stale);
            logger.LogInformation(
                "Removed stale book stub {Id} \"{Name}\" from series {SeriesId} \"{SeriesName}\" -- no longer in {Provider}'s book list",
                stale.Id, stale.Name, series.Id, series.Name, provider.Name);
        }
        await db.SaveChangesAsync(ct);
        if (staleStubs.Count > 0)
            subtree = subtree.Except(staleStubs).ToList();

        var mediaTypeName = series.MediaType.Name;
        var created = 0;
        foreach (var part in results)
        {
            if (string.IsNullOrEmpty(part.ExternalId) || string.IsNullOrEmpty(part.Title)) continue;

            var position = TryGetSeriesPosition(part.ExtendedData);

            var existing = subtree.FirstOrDefault(m => m.ExternalIds.Any(
                    e => string.Equals(e.ExternalId, part.ExternalId, StringComparison.OrdinalIgnoreCase)))
                ?? FindByNormalizedTitleYear(subtree, part);

            if (existing is not null)
            {
                if (manuallyPlacedIds.Contains(existing.Id))
                    continue; // the user's own placement (in or out of this series) always wins

                if (existing.Id == series.Id) continue; // paranoia guard, shouldn't happen

                var floorPosition = position.HasValue ? (int?)Math.Floor(position.Value) : null;
                var movingSeries = existing.ParentId != series.Id;
                var changed = movingSeries || existing.HierarchyLevel != 2 ||
                              (floorPosition.HasValue && existing.Number != floorPosition.Value);
                if (changed)
                {
                    var oldParentId = existing.ParentId;
                    existing.ParentId       = series.Id;
                    existing.HierarchyLevel = 2;
                    if (floorPosition.HasValue)
                        existing.Number = floorPosition.Value;
                    else if (movingSeries)
                        // Moved into a DIFFERENT series with no known position for it here --
                        // Hardcover's data can be incomplete (per-user direction), and the old
                        // series' number has no meaning in the new one, so it must not survive
                        // silently rather than being left for a human to notice is wrong.
                        existing.Number = null;
                    existing.UpdatedAt      = DateTime.UtcNow;
                    logger.LogInformation(
                        "Book {Id} \"{Name}\" placed in series \"{Series}\" ({SeriesId}) as #{Number} -- found already " +
                        "in the library (was parent={OldParent}) rather than creating a duplicate stub",
                        existing.Id, existing.Name, series.Name, series.Id, position, oldParentId?.ToString() ?? "root");
                    await db.SaveChangesAsync(ct);

                    if (oldParentId.HasValue && oldParentId.Value != series.Id && oldParentId.Value != authorId)
                        await RemoveOrphanedSeriesAsync(db, oldParentId.Value, series.MediaTypeId, ct);
                }
                continue;
            }

            var stub = new MediaItem
            {
                MediaTypeId    = series.MediaTypeId,
                ParentId       = series.Id,
                HierarchyLevel = 2,
                Name           = part.Title,
                Year           = part.Year,
                Number         = position.HasValue ? (int)Math.Floor(position.Value) : null,
                PosterUrl      = part.PosterUrl,
                IsStub         = true,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
                ExternalIds    = [new MediaExternalId { Source = providerSource, ExternalId = part.ExternalId }],
            };
            foreach (var (mpPluginId, mpProvider) in allProviders ?? [])
            {
                var supported = mpProvider.GetSupportedMediaTypes()
                    .Any(s => string.Equals(s.MediaTypeName, mediaTypeName, StringComparison.OrdinalIgnoreCase));
                if (!supported) continue;
                db.MediaEnrichments.Add(new MediaItemEnrichment
                {
                    MediaItem = stub, PluginId = mpPluginId, Status = EnrichmentStatus.Pending, MaxRetries = 3,
                });
            }
            db.MediaItems.Add(stub);
            await db.SaveChangesAsync(ct);
            subtree.Add(stub);
            created++;
            logger.LogInformation(
                "Created book stub {StubId} \"{Name}\" (#{Number}) under series {SeriesId} \"{SeriesName}\"",
                stub.Id, stub.Name, position, series.Id, series.Name);
        }

        if (created > 0 || staleStubs.Count > 0)
            logger.LogInformation(
                "Series {SeriesId} \"{SeriesName}\": created {Created} stub(s), removed {Removed} stale stub(s)",
                series.Id, series.Name, created, staleStubs.Count);

        return true;
    }

    public async Task ReparentIntoSeriesAsync(
        ChronicleDbContext db, int bookId, int seriesId, CancellationToken ct = default)
    {
        var book = await db.MediaItems.Include(m => m.MediaType)
            .FirstOrDefaultAsync(m => m.Id == bookId, ct) ?? throw new MediaNotFoundException(bookId);
        var series = await db.MediaItems.Include(m => m.MediaType)
            .FirstOrDefaultAsync(m => m.Id == seriesId, ct) ?? throw new MediaNotFoundException(seriesId);

        if (book.MediaType?.HierarchyLevels < 3 || series.MediaType?.HierarchyLevels < 3)
            throw new InvalidOperationException(
                "Both items must be an Author -> Series -> Book type to use series membership.");
        if (book.MediaTypeId != series.MediaTypeId)
            throw new InvalidOperationException("The book and the series must be the same media type.");
        if (series.HierarchyLevel != 1 || series.ParentId is null)
            throw new InvalidOperationException("The target is not a series.");
        // A series and a standalone book are structurally identical (both level 1, same author)
        // until the series actually has a book under it -- without this check, the picker (which
        // can't otherwise tell the two apart either, see MediaDetailPage's own comment on it)
        // could nest one standalone book under another, silently inventing a fake series.
        if (!await db.MediaItems.AnyAsync(m => m.ParentId == series.Id, ct))
            throw new InvalidOperationException("The target has no books of its own yet, so it isn't an existing series to add to.");
        if (book.HierarchyLevel != 1 || book.ParentId != series.ParentId)
            throw new InvalidOperationException(
                "This book is not a standalone book by the series' own author -- remove it from its current series first.");
        if (book.Id == series.Id)
            throw new InvalidOperationException("A book can't be its own series.");

        book.ParentId       = series.Id;
        book.HierarchyLevel = 2;
        book.UpdatedAt      = DateTime.UtcNow;

        var alreadyMarked = await db.MediaExternalIds.AnyAsync(
            e => e.MediaItemId == book.Id && e.ExternalId == ManualSeriesMemberMarker, ct);
        if (!alreadyMarked)
            db.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = book.Id, Source = "chronicle", ExternalId = ManualSeriesMemberMarker,
            });

        await ResetEnrichmentAsync(db, book.Id, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Book {BookId} \"{Name}\" manually added to series {SeriesId} \"{SeriesName}\"",
            book.Id, book.Name, series.Id, series.Name);
    }

    public async Task RemoveFromSeriesAsync(ChronicleDbContext db, int bookId, CancellationToken ct = default)
    {
        var book = await db.MediaItems.Include(m => m.MediaType)
            .FirstOrDefaultAsync(m => m.Id == bookId, ct) ?? throw new MediaNotFoundException(bookId);

        if (book.HierarchyLevel != 2 || book.ParentId is null)
            throw new InvalidOperationException($"Book {bookId} is not currently in a series.");
        if (book.MediaType?.HierarchyLevels < 3)
            throw new InvalidOperationException("Only an Author -> Series -> Book type supports series membership.");

        var series = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == book.ParentId.Value, ct)
            ?? throw new InvalidOperationException($"Book {bookId}'s series {book.ParentId} was not found.");
        var authorId = series.ParentId;

        book.ParentId       = authorId;
        book.HierarchyLevel = 1;
        book.Number         = null;
        book.UpdatedAt      = DateTime.UtcNow;

        // NOT sticky, per user direction: clears the marker too, mirroring
        // IMovieCollectionService.UnparentFromCollectionAsync exactly -- the book is fully
        // auto-manageable again the next time EnsureSeriesStubsAsync runs.
        var marker = await db.MediaExternalIds.FirstOrDefaultAsync(
            e => e.MediaItemId == book.Id && e.ExternalId == ManualSeriesMemberMarker, ct);
        if (marker is not null) db.MediaExternalIds.Remove(marker);

        await ResetEnrichmentAsync(db, book.Id, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Book {BookId} \"{Name}\" removed from series {SeriesId} \"{SeriesName}\"",
            book.Id, book.Name, series.Id, series.Name);

        if (authorId.HasValue)
            await RemoveOrphanedSeriesAsync(db, series.Id, book.MediaTypeId, ct);
    }

    private static async Task ResetEnrichmentAsync(ChronicleDbContext db, int itemId, CancellationToken ct)
    {
        var rows = await db.MediaEnrichments.Where(e => e.MediaItemId == itemId).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status = EnrichmentStatus.Pending;
            row.RetryCount = 0;
            row.LastAttemptedAt = null;
            row.ErrorMessage = null;
        }
    }

    private async Task RemoveOrphanedSeriesAsync(
        ChronicleDbContext db, int candidateId, int mediaTypeId, CancellationToken ct)
    {
        var candidate = await db.MediaItems.FirstOrDefaultAsync(
            m => m.Id == candidateId && m.MediaTypeId == mediaTypeId && m.HierarchyLevel == 1, ct);
        if (candidate is null) return;

        var hasChildren = await db.MediaItems.AnyAsync(m => m.ParentId == candidateId, ct);
        if (hasChildren) return;

        var extIds = await db.MediaExternalIds.Where(e => e.MediaItemId == candidateId).ToListAsync(ct);
        db.MediaExternalIds.RemoveRange(extIds);
        db.MediaItems.Remove(candidate);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Removed orphaned series {Id} \"{Name}\" -- no remaining children", candidate.Id, candidate.Name);
    }

    private static MediaItem? FindByNormalizedTitleYear(List<MediaItem> subtree, MediaMetadata part)
    {
        var normalized = MediaItemNormalizer.NormalizeName(part.Title);
        if (string.IsNullOrEmpty(normalized)) return null;
        var candidates = part.Year.HasValue ? subtree.Where(m => m.Year == part.Year) : subtree;
        return candidates.FirstOrDefault(m => MediaItemNormalizer.NormalizeName(m.Name) == normalized);
    }

    private static double? TryGetSeriesPosition(JsonElement? extendedData)
    {
        if (extendedData is not { } el || el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("seriesPosition", out var pos)) return null;
        return pos.ValueKind == JsonValueKind.Number ? pos.GetDouble() : null;
    }

    private async Task PersistSeriesMetadataAsync(
        ChronicleDbContext db, MediaItem series, MediaMetadata meta, string pluginId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return;
        var hasArtwork = meta.PosterUrl is not null || meta.AdditionalImages.Count > 0;
        if (!hasArtwork && string.IsNullOrWhiteSpace(meta.Overview)) return;

        try
        {
            var blobs = MetadataResolutionService.ParsePluginBlobs(series.MetadataJson);
            var shortSource = PluginIdHelper.ToSource(pluginId);
            if (!string.Equals(shortSource, pluginId, StringComparison.OrdinalIgnoreCase))
                blobs.Remove(shortSource);

            blobs[pluginId] = JsonSerializer.SerializeToElement(new
            {
                title            = meta.Title,
                overview         = meta.Overview,
                posterUrl        = meta.PosterUrl,
                additionalImages = meta.AdditionalImages,
            });

            series.MetadataJson = JsonSerializer.Serialize(blobs);
            series.UpdatedAt    = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to persist series metadata for {SeriesId} \"{Name}\"", series.Id, series.Name);
        }
    }
}
