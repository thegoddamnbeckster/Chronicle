using Chronicle.API.DTOs;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Helpers;

/// <summary>
/// Batch ancestor-chain resolution shared by any endpoint that needs to show an item's
/// full parent context (e.g. "Show › Season" for an episode) without an N+1 query per item.
/// </summary>
internal static class AncestorHelper
{
    /// <summary>
    /// Root-first ancestor list for each of <paramref name="startIds"/>, keyed by that id.
    /// Walks the parent chain breadth-first so items sharing ancestors (e.g. many episodes
    /// of the same season) only load each shared row once, regardless of how many start ids
    /// are passed in. 10-level ceiling and cycle guard mirror MediaController.BuildAncestorsAsync.
    /// </summary>
    public static async Task<Dictionary<int, List<AncestorDto>>> BuildAncestorsBatchAsync(
        ChronicleDbContext db, IEnumerable<int> startIds, CancellationToken ct = default)
    {
        var ids = startIds.Distinct().ToList();
        var result = new Dictionary<int, List<AncestorDto>>();
        if (ids.Count == 0) return result;

        var known = new Dictionary<int, (string Name, int? ParentId)>();
        var frontier = new HashSet<int>(ids);

        for (var depth = 0; depth < 10 && frontier.Count > 0; depth++)
        {
            var toLoad = frontier.Where(id => !known.ContainsKey(id)).ToList();
            if (toLoad.Count == 0) break;

            var rows = await db.MediaItems
                .Where(m => toLoad.Contains(m.Id))
                .Select(m => new { m.Id, m.Name, m.ParentId })
                .ToListAsync(ct);

            frontier.Clear();
            foreach (var row in rows)
            {
                known[row.Id] = (row.Name, row.ParentId);
                if (row.ParentId.HasValue && !known.ContainsKey(row.ParentId.Value))
                    frontier.Add(row.ParentId.Value);
            }
        }

        foreach (var id in ids)
        {
            var chain = new List<AncestorDto>();
            if (!known.TryGetValue(id, out var self))
            {
                result[id] = chain;
                continue;
            }

            var visited = new HashSet<int>();
            var parentId = self.ParentId;
            while (parentId.HasValue && chain.Count < 10 && visited.Add(parentId.Value))
            {
                if (!known.TryGetValue(parentId.Value, out var node)) break;
                chain.Insert(0, new AncestorDto(parentId.Value, node.Name));
                parentId = node.ParentId;
            }
            result[id] = chain;
        }

        return result;
    }
}
