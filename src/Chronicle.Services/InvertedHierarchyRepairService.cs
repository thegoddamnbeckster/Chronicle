using System.Text.Json.Nodes;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: a level-1 item (an album, a season, a series) whose parent is a level-1 or level-2
/// item (a track, an episode) is moved under a root-level item of the right name, created if the library has none.
///
/// Root-caused live (2026-10-15): scanning the band "Genesis" matched the artist to a TRACK named "Genesis" (from another artist's
/// album) because the root name lookup was not limited to root-level items, and ninety-four of the band's albums were filed
/// under that track. "Ghost", "Rush" and "NIRVANA" had the same story. The scan is fixed (see FileScanService.RootLevelCeiling);
/// this heals what it already did.
///
/// The folders say where the children belong: when every child sits in a folder directly inside one folder whose NAME is the
/// wrong parent's name (E:\Music\Genesis\Foxtrot, E:\Music\Genesis\Duke ... under the track "Genesis"), that folder is the real
/// artist. A group whose folders disagree, or do not carry the parent's name, is left alone rather than guessed.
/// </summary>
public sealed class InvertedHierarchyRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<InvertedHierarchyRepairService> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var fileScan = scope.ServiceProvider.GetService<IFileScanService>();

        var suspects = await (
                from m in db.MediaItems
                join p in db.MediaItems on m.ParentId equals p.Id
                where m.HierarchyLevel == 1 && p.HierarchyLevel >= 1 && m.MediaType!.HierarchyLevels >= 3
                select new { Child = m, Parent = p })
            .ToListAsync(ct);
        if (suspects.Count == 0)
        {
            logger.LogInformation("Inverted hierarchy repair: nothing to repair");
            return;
        }

        int moved = 0, created = 0, deleted = 0;
        foreach (var group in suspects.GroupBy(s => s.Parent.Id))
        {
            ct.ThrowIfCancellationRequested();
            var parent = group.First().Parent;
            var children = group.Select(g => g.Child).ToList();

            var folders = children
                .Select(c => MisparentedChildRepairService.NormalizeFolder(
                    MisparentedChildRepairService.ParentFolder(MisparentedChildRepairService.FolderOf(c.MetadataJson))))
                .Distinct().ToList();
            if (folders.Count != 1 || folders[0] is not { } folder || !FolderIsNamed(folder, parent.Name))
            {
                // No folder to go by (a season a provider created, not a scan). When the wrong parent is an episode that sits directly under a
                // root (a show with no season level), the root is the one parent that keeps every level in order.
                var grandparent = parent.HierarchyLevel == 2 && parent.ParentId is { } gpId
                    ? await db.MediaItems.FirstOrDefaultAsync(g => g.Id == gpId, ct)
                    : null;
                if (grandparent is { HierarchyLevel: 0 } && grandparent.MediaTypeId == parent.MediaTypeId)
                {
                    foreach (var child in children)
                    {
                        child.ParentId = grandparent.Id;
                        child.UpdatedAt = DateTime.UtcNow;
                    }
                    await db.SaveChangesAsync(ct);
                    moved += children.Count;
                    logger.LogInformation(
                        "Inverted hierarchy repair: {Count} item(s) were filed under the episode \"{Parent}\" ({ParentId}); moved up to its show {Root} ({RootId})",
                        children.Count, parent.Name, parent.Id, grandparent.Name, grandparent.Id);
                    continue;
                }

                // A provider-made copy of the season the episode already sits in (a second "Season 8" under an episode of Season 8): no
                // file, no children, no id, nothing the user did. Removed rather than kept as a duplicate of its own grandparent.
                if (parent.ParentId is { } seasonId && await RemoveStrayCopiesOfSeasonAsync(db, seasonId, children, ct) is { } removed)
                {
                    deleted += removed;
                    continue;
                }

                logger.LogInformation(
                    "Inverted hierarchy repair: {Count} item(s) under \"{Parent}\" ({ParentId}) are in no single folder of that name; left alone",
                    children.Count, parent.Name, parent.Id);
                continue;
            }

            var nameLower = parent.Name.ToLower();
            var roots = await db.MediaItems
                .Where(r => r.HierarchyLevel == 0 && r.ParentId == null && r.MediaTypeId == parent.MediaTypeId && r.Name.ToLower() == nameLower)
                .ToListAsync(ct);
            if (roots.Count > 1)
            {
                logger.LogInformation("Inverted hierarchy repair: {Count} roots are named \"{Parent}\"; left alone", roots.Count, parent.Name);
                continue;
            }

            var root = roots.SingleOrDefault();
            if (root is null)
            {
                root = new MediaItem
                {
                    MediaTypeId = parent.MediaTypeId, HierarchyLevel = 0, ParentId = null, Name = parent.Name,
                    MetadataJson = new JsonObject
                    {
                        ["fileScanner"] = new JsonObject { ["importedAt"] = DateTime.UtcNow, ["filePaths"] = new JsonArray(), ["folderPath"] = RestoreCase(folder, children) },
                    }.ToJsonString(),
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                };
                db.MediaItems.Add(root);
                await db.SaveChangesAsync(ct);
                created++;

                if (fileScan is not null)
                {
                    var typeName = await db.MediaTypes.Where(t => t.Id == parent.MediaTypeId).Select(t => t.Name).FirstAsync(ct);
                    await fileScan.SeedPendingEnrichmentAsync([root.Id], typeName, ct);
                }
            }

            foreach (var child in children)
            {
                child.ParentId = root.Id;
                child.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            moved += children.Count;
            logger.LogInformation(
                "Inverted hierarchy repair: {Count} item(s) were filed under \"{Parent}\" ({ParentId}), which is a level-{Level} item; moved under {Root} ({RootId})",
                children.Count, parent.Name, parent.Id, parent.HierarchyLevel, root.Name, root.Id);
        }

        logger.LogInformation("Inverted hierarchy repair: moved {Moved} item(s), created {Created} root item(s), removed {Deleted} stray copy(ies)", moved, created, deleted);
    }

    /// <summary>
    /// Removes <paramref name="children"/> when each one is an empty placeholder with the same name as the level-1 item
    /// <paramref name="seasonId"/> (its wrong parent's own parent). Returns the number removed, or null when any child is more than that
    /// (then nothing is removed).
    /// </summary>
    private async Task<int?> RemoveStrayCopiesOfSeasonAsync(ChronicleDbContext db, int seasonId, List<MediaItem> children, CancellationToken ct)
    {
        var season = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(s => s.Id == seasonId, ct);
        if (season is not { HierarchyLevel: 1 }) return null;
        if (children.Any(c => c.MediaTypeId != season.MediaTypeId || !string.Equals(c.Name.Trim(), season.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return null;

        // The copies and whatever hangs under them (a copy of the episode, under a copy of the season, under the episode): all of it
        // must be empty placeholders, one level deep at most (the copy of the episode), or nothing is removed.
        var strayIds = children.Select(c => c.Id).ToList();
        var below = await db.MediaItems.Where(k => k.ParentId != null && strayIds.Contains(k.ParentId.Value)).ToListAsync(ct);
        var belowIds = below.Select(b => b.Id).ToList();
        if (belowIds.Count > 0 && await db.MediaItems.AnyAsync(k => k.ParentId != null && belowIds.Contains(k.ParentId.Value), ct)) return null;

        var all = children.Concat(below).ToList();
        var ids = all.Select(c => c.Id).ToList();
        if (all.Any(c => Scan.FileIdentityJson.HasKnownFile(c.MetadataJson) || !string.IsNullOrEmpty(Scan.FileIdentityJson.PrimaryFilePathKey(c.MetadataJson))))
            return null;
        if (await db.MediaExternalIds.AnyAsync(e => ids.Contains(e.MediaItemId), ct)) return null;
        if (await db.MediaEnrichments.AnyAsync(e => ids.Contains(e.MediaItemId) && e.Status == EnrichmentStatus.Completed, ct)) return null;
        if ((await BookSeriesService.StubIdsWithUserDataAsync(db, ids, ct)).Count > 0) return null;

        db.MediaEnrichments.RemoveRange(db.MediaEnrichments.Where(e => ids.Contains(e.MediaItemId)));
        db.MediaItems.RemoveRange(below);
        db.MediaItems.RemoveRange(children);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Inverted hierarchy repair: removed {Count} empty copy(ies) of \"{Season}\" ({SeasonId}) filed under its own episodes",
            children.Count, season.Name, season.Id);
        return children.Count + below.Count;
    }

    /// <summary>True when the last segment of <paramref name="normalizedFolder"/> is <paramref name="name"/>, ignoring case.</summary>
    internal static bool FolderIsNamed(string normalizedFolder, string name)
    {
        var i = normalizedFolder.LastIndexOf('\\');
        var last = i < 0 ? normalizedFolder : normalizedFolder[(i + 1)..];
        return string.Equals(last, name.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folder with its original capitalisation (the normalised form is lower case), taken from a child's own record.</summary>
    private static string RestoreCase(string normalizedFolder, List<MediaItem> children)
    {
        foreach (var child in children)
        {
            var original = MisparentedChildRepairService.ParentFolder(MisparentedChildRepairService.FolderOf(child.MetadataJson));
            if (original is not null && MisparentedChildRepairService.NormalizeFolder(original) == normalizedFolder)
                return original;
        }
        return normalizedFolder;
    }
}
