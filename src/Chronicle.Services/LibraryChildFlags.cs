using Chronicle.Data;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

/// <summary>For one root item of a library page: whether it has children, and whether the leaves under it include a file.</summary>
public readonly record struct ChildFileFlags(bool HasChildren, bool ChildrenHaveFile);

/// <summary>
/// What a library page needs to know about each root's descendants (is it a container, does anything under it have a file),
/// answered WITHOUT loading the descendants' metadata: a page of 24 music artists used to pull ~58 MB of blobs out of the
/// database and parse them, seconds per page. Only rows that mention a file at all are even considered, a few at a time,
/// stopping at the first real one. The rule is unchanged: the leaves are the grandchildren when there are any, else the
/// direct children.
/// </summary>
public static class LibraryChildFlags
{
    public static async Task<Dictionary<int, ChildFileFlags>> LoadAsync(ChronicleDbContext db, IReadOnlyCollection<int> rootIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, ChildFileFlags>();
        if (rootIds.Count == 0) return result;

        var direct = await db.MediaItems.AsNoTracking()
            .Where(m => m.ParentId != null && rootIds.Contains(m.ParentId.Value))
            .Select(m => new { m.Id, ParentId = m.ParentId!.Value })
            .ToListAsync(ct);

        foreach (var group in direct.GroupBy(d => d.ParentId))
        {
            var childIds = group.Select(d => d.Id).ToList();
            var hasGrandchildren = await db.MediaItems.AsNoTracking()
                .AnyAsync(m => m.ParentId != null && childIds.Contains(m.ParentId.Value), ct);
            var leafParents = hasGrandchildren ? childIds : new List<int> { group.Key };
            result[group.Key] = new ChildFileFlags(true, await AnyChildHasKnownFileAsync(db, leafParents, ct));
        }
        return result;
    }

    private static async Task<bool> AnyChildHasKnownFileAsync(ChronicleDbContext db, List<int> parentIds, CancellationToken ct)
    {
        var candidates = db.MediaItems.AsNoTracking()
            .Where(m => m.ParentId != null && parentIds.Contains(m.ParentId.Value) && m.MetadataJson != null
                        && (EF.Functions.Like(m.MetadataJson, "%filePaths%") || EF.Functions.Like(m.MetadataJson, "%scraperResolvedFile%")));
        var lastId = 0;
        while (true)
        {
            var chunk = await candidates.Where(m => m.Id > lastId).OrderBy(m => m.Id).Take(25)
                .Select(m => new { m.Id, m.MetadataJson }).ToListAsync(ct);
            if (chunk.Count == 0) return false;
            if (chunk.Any(c => FileIdentityJson.HasKnownFile(c.MetadataJson))) return true;
            lastId = chunk[^1].Id;
        }
    }
}
