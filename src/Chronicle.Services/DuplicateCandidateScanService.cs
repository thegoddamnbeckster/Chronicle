using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Nightly background task that scans for probable duplicate MediaItem pairs and caches them
/// in media_item_duplicate_candidates for the Duplicates UI page. Two passes:
/// 1. Same media type + hierarchy level + parent + normalized name (the original check).
/// 2. Cross-type root items with the same name (see AddCrossTypeCandidates's own doc) --
///    confirmed live (2026-09-04): a bad Kodi movie-library scrape created ~95 flat
///    "movies"-typed duplicates of already-correct "tv" items (Rick and Morty, The
///    Mandalorian, Foundation, ...) that pass 1 could never catch, since it only ever
///    compares items of the SAME type against each other.
/// Either way, this service only ever POPULATES the review queue -- nothing here deletes or
/// merges anything; a human always makes that call from the Duplicates page.
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

        // Load all items with their grouping key (include Year for mismatch filtering).
        // HasExternalId is a correlated EXISTS subquery (EF Core translates it as SQL, not an
        // N+1) -- both it and IsStub feed AddCrossTypeCandidates's "is either side actually
        // verified yet" check below.
        var items = await db.MediaItems
            .Where(m => m.NormalizedName != null && m.NormalizedName != string.Empty)
            .Select(m => new
            {
                m.Id, m.NormalizedName, m.NormalizedNameLoose, m.MediaTypeId, m.HierarchyLevel,
                m.ParentId, m.Year, m.IsStub,
                HasExternalId = db.MediaExternalIds.Any(e => e.MediaItemId == m.Id),
            })
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
                    // If both items have a known year and the years differ, they are
                    // different works (e.g. Aladdin 1992 vs Aladdin 2019) — not duplicates.
                    var yearI = list[i].Year;
                    var yearJ = list[j].Year;
                    if (yearI.HasValue && yearJ.HasValue && yearI != yearJ)
                        continue;

                    var a = Math.Min(list[i].Id, list[j].Id);
                    var b = Math.Max(list[i].Id, list[j].Id);
                    if (!dismissedSet.Contains((a, b)))
                        newCandidates.Add((a, b));
                }
            }
        }

        // Cross-type pass: the same real-world title can end up typed as BOTH e.g. "movies"
        // and "tv" -- confirmed live (2026-09-04): a bad Kodi movie-library scrape created ~95
        // flat "movies" duplicates of already-correct TV shows (Rick and Morty, The
        // Mandalorian, Foundation, ...) that the pass above could never catch, since it only
        // ever compares items of the SAME type against each other.
        //
        // Cross-type name collisions are far more likely to be genuinely different works than
        // same-type ones are (a movie and an unrelated TV show sharing a title is common; a
        // soundtrack album named after its movie is common; two items of the exact same type
        // sharing an exact name rarely is) -- per-user request (2026-09-04): "it should not
        // flag movies and tv shows that are genuinely the same name... use as much of the
        // metadata that it can to ensure which one is which". So this pass requires real
        // corroboration beyond just the name, and is deliberately stricter than the same-type
        // pass above in two ways:
        //   - Year must be present AND equal on BOTH sides (the same-type pass above still
        //     flags a pair where one side's year is simply unknown; here that's not enough).
        //   - At least one side must be otherwise unverified (IsStub, or literally zero rows in
        //     media_external_ids) -- the actual fingerprint of a phantom scrape duplicate,
        //     never enriched against any real provider. Two items that are BOTH independently
        //     matched against real metadata (even if coincidentally same name and year) are
        //     left alone rather than flagged -- getting that case wrong risks exactly what the
        //     request above was guarding against, so it's excluded rather than surfaced.
        // Scoped to HierarchyLevel 0 (top-level items) only -- comparing seasons/episodes
        // across unrelated shows this way would be meaningless.
        //
        // Local function, capturing `items`/`dismissedSet`/`newCandidates` from ExecuteAsync's
        // own scope rather than taking parameters -- all three are anonymously-typed locals
        // with no nameable shared type to declare a parameter as. Like the pass above, this
        // only adds to the SAME review queue a human acts on from the Duplicates page --
        // nothing here deletes or merges anything itself.
        void AddCrossTypeCandidates()
        {
            var byLooseName = items
                .Where(m => m.HierarchyLevel == 0 && !string.IsNullOrEmpty(m.NormalizedNameLoose))
                .GroupBy(m => m.NormalizedNameLoose);
            foreach (var nameGroup in byLooseName)
            {
                var list = nameGroup.ToList();
                if (list.Count < 2) continue;
                for (int i = 0; i < list.Count - 1; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (list[i].MediaTypeId == list[j].MediaTypeId)
                        continue; // same-type pairs are already covered by the pass above

                    if (list[i].Year is null || list[j].Year is null || list[i].Year != list[j].Year)
                        continue;

                    var iUnverified = list[i].IsStub || !list[i].HasExternalId;
                    var jUnverified = list[j].IsStub || !list[j].HasExternalId;
                    if (!iUnverified && !jUnverified)
                        continue; // both sides independently verified -- too risky to assume duplicate

                    var a = Math.Min(list[i].Id, list[j].Id);
                    var b = Math.Max(list[i].Id, list[j].Id);
                    if (!dismissedSet.Contains((a, b)))
                        newCandidates.Add((a, b));
                }
            }
        }
        AddCrossTypeCandidates();

        // Replace candidates table atomically — delete old, insert new in one transaction
        // so a failure mid-insert doesn't leave an empty candidates table.
        var distinctCandidates = newCandidates.Distinct().ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.MediaItemDuplicateCandidates.ToListAsync(ct);
        db.MediaItemDuplicateCandidates.RemoveRange(existing);
        foreach (var (a, b) in distinctCandidates)
            db.MediaItemDuplicateCandidates.Add(new Core.Models.MediaItemDuplicateCandidate
            {
                ItemAId    = a,
                ItemBId    = b,
                DetectedAt = DateTime.UtcNow,
            });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation(
            "Duplicate candidate scan complete: {Count} candidates stored", distinctCandidates.Count);
    }
}
