using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: removes ghost items -- things the file scanner once created
/// whose file is gone and that hold nothing else.
///
/// A folder that is renamed or moved ("Hell Divers Series - 1 - ..." becoming "Hell Divers - 1 - ...") is imported
/// again under its new name, and Missing Source Reconciliation clears the dead file link from the old item but keeps
/// the item. With no provider id, no completed lookup and nothing the user did, what is left is a name and a number:
/// "Hell Divers Series" with nine books all called "Hell Divers Series", beside the real "Hell Divers"; an item
/// named after the author, filed as one of the author's series. 61 such items were in the library.
///
/// Only an unmistakable ghost is removed: it was imported by the scanner, holds no file now, has no external id of any
/// provider, no completed lookup, no children, nothing the user rated, marked, listed or played, and was not placed in
/// its series by hand. A series left empty by this, and holding nothing either, goes with its books. Only types whose
/// own level names include "Series" (Author, Series, Book) are considered.
/// </summary>
public sealed class GhostItemRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<GhostItemRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var candidates = await db.MediaItems
            .Include(m => m.ExternalIds)
            .Where(m => m.HierarchyLevel >= 1 && !m.IsStub && m.ParentId != null &&
                        m.MetadataJson != null && m.MetadataJson.Contains("fileScanner") &&
                        m.MediaType!.HierarchyLabels != null && m.MediaType.HierarchyLabels.Contains("Series"))
            .ToListAsync(ct);
        candidates = candidates.Where(m => !FileIdentityJson.HasKnownFile(m.MetadataJson) && IsBareSeriesLevelType(m)).ToList();

        var withChildren = (await db.MediaItems
                .Where(c => c.ParentId != null && candidates.Select(m => m.Id).Contains(c.ParentId!.Value))
                .Select(c => c.ParentId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var completed = (await db.MediaEnrichments
                .Where(e => e.Status == EnrichmentStatus.Completed && candidates.Select(m => m.Id).Contains(e.MediaItemId))
                .Select(e => e.MediaItemId).Distinct().ToListAsync(ct)).ToHashSet();
        var owned = await BookSeriesService.StubIdsWithUserDataAsync(db, candidates.Select(m => m.Id), ct);

        var ghosts = candidates.Where(m =>
                !withChildren.Contains(m.Id) && !completed.Contains(m.Id) && !owned.Contains(m.Id) &&
                m.ExternalIds.All(e => string.Equals(e.Source, "chronicle", StringComparison.OrdinalIgnoreCase) &&
                                       e.ExternalId != BookSeriesService.ManualSeriesMemberMarker))
            .ToList();

        var parentIds = ghosts.Select(g => g.ParentId!.Value).Distinct().ToList();
        foreach (var ghost in ghosts)
        {
            db.MediaExternalIds.RemoveRange(ghost.ExternalIds);
            db.MediaEnrichments.RemoveRange(db.MediaEnrichments.Where(e => e.MediaItemId == ghost.Id));
            db.MediaItems.Remove(ghost);
        }
        if (ghosts.Count > 0) await db.SaveChangesAsync(ct);

        // A series the ghosts were all of: nothing left under it, no file, nothing of the user's.
        var emptied = 0;
        foreach (var parentId in parentIds)
        {
            var parent = await db.MediaItems.Include(m => m.ExternalIds).FirstOrDefaultAsync(m => m.Id == parentId, ct);
            if (parent is null || parent.HierarchyLevel != 1 || FileIdentityJson.HasKnownFile(parent.MetadataJson)) continue;
            if (await db.MediaItems.AnyAsync(m => m.ParentId == parentId, ct)) continue;
            if (parent.ExternalIds.Any(e => e.ExternalId == BookSeriesService.ManualSeriesMemberMarker)) continue;
            if ((await BookSeriesService.StubIdsWithUserDataAsync(db, [parentId], ct)).Count > 0) continue;

            db.MediaExternalIds.RemoveRange(parent.ExternalIds);
            db.MediaEnrichments.RemoveRange(db.MediaEnrichments.Where(e => e.MediaItemId == parentId));
            db.MediaItems.Remove(parent);
            emptied++;
            logger.LogInformation("Ghost item repair: removed the emptied series \"{Name}\" ({Id})", parent.Name, parentId);
        }
        if (emptied > 0) await db.SaveChangesAsync(ct);

        logger.LogInformation("Ghost item repair: removed {Ghosts} ghost item(s) and {Series} emptied series", ghosts.Count, emptied);
    }

    /// <summary>The type's second level is named "Series" (Author, Series, Book): the layout where a scanned folder can become a series or a book.</summary>
    private static bool IsBareSeriesLevelType(MediaItem m)
    {
        var labels = (m.MediaType?.HierarchyLabels ?? "").Split(',', StringSplitOptions.TrimEntries);
        return labels.Length > 1 && string.Equals(labels[1], "Series", StringComparison.OrdinalIgnoreCase);
    }
}
