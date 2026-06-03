using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Nightly background task that scans for probable duplicate MediaItem pairs
/// (same media type + hierarchy level + parent + normalized name)
/// and caches them in media_item_duplicate_candidates for the Duplicates UI page.
/// </summary>
public sealed class DuplicateCandidateScanService(
    IServiceScopeFactory scopeFactory,
    ILogger<DuplicateCandidateScanService> logger) : IScheduledTask
{
    public string TaskId      => "duplicate_candidate_scan";
    public string DisplayName => "Duplicate Candidate Scan";
    public string Description => "Scans for probable duplicate media items by normalised name and caches the results for the Duplicates page.";
    public string DefaultCron => "0 2 * * *"; // 2 AM nightly

    public async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Starting duplicate candidate scan");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Load all items with their grouping key
        var items = await db.MediaItems
            .Where(m => m.NormalizedName != null && m.NormalizedName != string.Empty)
            .Select(m => new { m.Id, m.NormalizedName, m.MediaTypeId, m.HierarchyLevel, m.ParentId })
            .ToListAsync(ct);

        // Load existing dismissals to exclude
        var dismissedPairs = await db.MediaItemDuplicateDismissals
            .Select(d => new { d.ItemAId, d.ItemBId })
            .ToListAsync(ct);
        var dismissedSet = new HashSet<(int, int)>(
            dismissedPairs.Select(d => (Math.Min(d.ItemAId, d.ItemBId), Math.Max(d.ItemAId, d.ItemBId))));

        var newCandidates = new List<(int, int)>();

        // Group by (MediaTypeId, HierarchyLevel, ParentId) then find pairs with same normalized name
        var groups = items.GroupBy(m => (m.MediaTypeId, m.HierarchyLevel, m.ParentId));
        foreach (var group in groups)
        {
            var byName = group.GroupBy(m => m.NormalizedName);
            foreach (var nameGroup in byName)
            {
                var list = nameGroup.ToList();
                if (list.Count < 2) continue;
                for (int i = 0; i < list.Count - 1; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = Math.Min(list[i].Id, list[j].Id);
                    var b = Math.Max(list[i].Id, list[j].Id);
                    if (!dismissedSet.Contains((a, b)))
                        newCandidates.Add((a, b));
                }
            }
        }

        // Replace candidates table
        var existing = await db.MediaItemDuplicateCandidates.ToListAsync(ct);
        db.MediaItemDuplicateCandidates.RemoveRange(existing);
        foreach (var (a, b) in newCandidates.Distinct())
            db.MediaItemDuplicateCandidates.Add(new Core.Models.MediaItemDuplicateCandidate
            {
                ItemAId    = a,
                ItemBId    = b,
                DetectedAt = DateTime.UtcNow,
            });

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Duplicate candidate scan complete: {Count} candidates stored", newCandidates.Count);
    }
}
