using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Helpers;

internal record DescendantsResult(
    Dictionary<int, List<int>> DescendantIdsByRoot,
    Dictionary<int, string> NameById);

/// <summary>
/// Batch downward tree-walk (root → all descendants), the mirror image of
/// <see cref="AncestorHelper"/>. Used to find "is there a more specific descendant" —
/// e.g. does this tracked TV season have episodes with recorded activity — without an
/// N+1 query per root.
/// </summary>
internal static class DescendantHelper
{
    public static async Task<DescendantsResult> BuildDescendantsBatchAsync(
        ChronicleDbContext db, IEnumerable<int> rootIds, CancellationToken ct = default)
    {
        var roots = rootIds.Distinct().ToList();
        var descendantIdsByRoot = roots.ToDictionary(id => id, _ => new List<int>());
        var nameById = new Dictionary<int, string>();
        if (roots.Count == 0) return new DescendantsResult(descendantIdsByRoot, nameById);

        // parentId -> which root it traces back to. Starts as "each root traces to itself"
        // and advances one level per pass; a media tree can't have cycles (ParentId strictly
        // decreases HierarchyLevel), so no visited-guard is needed the way AncestorHelper needs
        // one (that walk follows user-editable ParentId pointers upward, this one is a single
        // query per level so a cycle would just terminate at the depth ceiling instead of
        // looping forever).
        var currentLevelParents = roots.ToDictionary(id => id, id => id);

        for (var depth = 0; depth < 10 && currentLevelParents.Count > 0; depth++)
        {
            var parentIds = currentLevelParents.Keys.ToList();
            var children = await db.MediaItems
                .Where(m => m.ParentId != null && parentIds.Contains(m.ParentId.Value))
                .Select(m => new { m.Id, m.Name, m.ParentId })
                .ToListAsync(ct);

            if (children.Count == 0) break;

            var nextLevelParents = new Dictionary<int, int>();
            foreach (var c in children)
            {
                var rootId = currentLevelParents[c.ParentId!.Value];
                descendantIdsByRoot[rootId].Add(c.Id);
                nameById[c.Id] = c.Name;
                nextLevelParents[c.Id] = rootId;
            }
            currentLevelParents = nextLevelParents;
        }

        return new DescendantsResult(descendantIdsByRoot, nameById);
    }
}
