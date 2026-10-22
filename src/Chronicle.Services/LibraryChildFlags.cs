using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

/// <summary>For one root item of a library page: whether it has children, and whether the leaves under it include a file.</summary>
public readonly record struct ChildFileFlags(bool HasChildren, bool ChildrenHaveFile);

/// <summary>
/// What a library page needs to know about each root's descendants (is it a container, does anything under it have a file),
/// answered WITHOUT loading the descendants' metadata and in a handful of set-based queries however many roots the page has
/// (a page of 24 music artists used to pull ~58 MB of blobs out of the database and parse them; a "show all" page of 250
/// artists or 4,000 films then ran two queries per root, seconds each time). Each parent is asked "does any child of yours
/// hold a file" with an EXISTS that stops at its first hit. The rule is unchanged: the leaves are the grandchildren when
/// there are any, else the direct children.
/// </summary>
public static class LibraryChildFlags
{
    // A file is recorded either by the scanner (fileScanner.filePaths, a non-empty array of strings) or by the Kodi scraper
    // (scraperResolvedFile.fileName). Matched on the stored JSON text, which is always written compact.
    private const string ScannerFilePath = "%\"filePaths\":[\"%";
    private const string ScraperFileName = "%\"scraperResolvedFile\":{%\"fileName\":\"%";

    public static async Task<Dictionary<int, ChildFileFlags>> LoadAsync(ChronicleDbContext db, IReadOnlyCollection<int> rootIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, ChildFileFlags>();
        if (rootIds.Count == 0) return result;

        var direct = await db.MediaItems.AsNoTracking()
            .Where(m => m.ParentId != null && rootIds.Contains(m.ParentId.Value))
            .Select(m => new { m.Id, ParentId = m.ParentId!.Value })
            .ToListAsync(ct);
        if (direct.Count == 0) return result;

        // which of those children have children of their own
        var childIds = direct.Select(d => d.Id).ToList();
        var parentsOfGrandchildren = (await db.MediaItems.AsNoTracking()
            .Where(c => childIds.Contains(c.Id) && db.MediaItems.Any(k => k.ParentId == c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct)).ToHashSet();

        // the parents of the leaves, per root: its children when it has grandchildren, else the root itself
        var leafParentsByRoot = direct.GroupBy(d => d.ParentId).ToDictionary(
            g => g.Key,
            g => g.Any(d => parentsOfGrandchildren.Contains(d.Id)) ? g.Select(d => d.Id).ToList() : new List<int> { g.Key });

        var allLeafParents = leafParentsByRoot.Values.SelectMany(x => x).Distinct().ToList();
        var parentsWithFile = (await db.MediaItems.AsNoTracking()
            .Where(p => allLeafParents.Contains(p.Id) && db.MediaItems.Any(k => k.ParentId == p.Id && k.MetadataJson != null
                        && (EF.Functions.Like(k.MetadataJson, ScannerFilePath) || EF.Functions.Like(k.MetadataJson, ScraperFileName))))
            .Select(p => p.Id)
            .ToListAsync(ct)).ToHashSet();

        foreach (var (root, leafParents) in leafParentsByRoot)
            result[root] = new ChildFileFlags(true, leafParents.Any(parentsWithFile.Contains));
        return result;
    }
}
