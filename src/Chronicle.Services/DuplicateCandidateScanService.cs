using Chronicle.Data;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Nightly background task that scans for probable duplicate MediaItem pairs and caches them
/// in media_item_duplicate_candidates for the Duplicates UI page. Passes:
/// 1. Same media type + hierarchy level + parent + normalized name (the original check).
/// 2. Cross-type root items with the same name (see AddCrossTypeCandidates's own doc) --
///    confirmed live (2026-09-04): a bad Kodi movie-library scrape created ~95 flat
///    "movies"-typed duplicates of already-correct "tv" items (Rick and Morty, The
///    Mandalorian, Foundation, ...) that pass 1 could never catch, since it only ever
///    compares items of the SAME type against each other.
/// 3. Same-parent, same-Number CONTAINERS (seasons/albums/etc, not leaf episodes/tracks) --
///    see AddSameParentSameNumberCandidates's own doc. Closes a gap Pass 1 cannot: two
///    container siblings can share the exact same season/album Number while being named
///    differently enough that NormalizedName never matches them (e.g. "Season 2" vs
///    "Season 02"), which is exactly the shape of a real 2026-08-03 corruption incident
///    (duplicate season containers for Renovation Resort/Battle on the Beach/Dark Matter)
///    that produced hundreds of fabricated episode records under the loser container before
///    anyone noticed -- root-caused and manually cleaned up 2026-09-13. A container's Number
///    IS its whole identity under its parent (see FileScanService.UpsertGroupItemAsync's own
///    "tertiary-and-a-half" tier, which already treats it that way for exactly this reason),
///    so two containers sharing one are duplicates regardless of what their Names say.
/// Every pass only ever POPULATES the review queue -- nothing here deletes or merges
/// anything; a human always makes that call from the Duplicates page. This is a deliberate
/// choice for container-level collisions too: an automatic merge risks combining genuinely
/// different episodes under one surviving season (see the project's "delete, don't merge"
/// policy for MediaItem dedup), so this stays detect-only like everything else here.
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
                m.ParentId, m.Year, m.IsStub, m.Number,
                HasExternalId = db.MediaExternalIds.Any(e => e.MediaItemId == m.Id),
            })
            .ToListAsync(ct);

        // Needed by AddSameParentSameNumberCandidates to tell a container level (season,
        // album, ...) apart from a leaf level (episode, track, ...) for each item's own media
        // type -- read from the DB rather than hardcoded, per this project's "no hardcoding"
        // convention, since different media types have different hierarchy depths.
        var hierarchyLevelsByType = await db.MediaTypes
            .Select(t => new { t.Id, t.HierarchyLevels })
            .ToDictionaryAsync(t => t.Id, t => t.HierarchyLevels, ct);

        // Separate, UNFILTERED load for AddSameParentSameNumberCandidates -- deliberately not
        // reusing `items` above, which excludes blank-NormalizedName rows for the name-matching
        // passes' own purposes. Caught in review before release: DuplicateCleanupService's own
        // Pass 5 queries all parented items with no such filter, so a corrupted/fabricated leaf
        // with a blank Name (exactly the shape of the 2026-08-03 incident this feature targets)
        // was visible to Cleanup's overlap check but invisible to this one -- Cleanup would
        // correctly detect the real overlap and defer to manual review, while this pass,
        // blind to that same child, judged the pair "no overlap, safe" and never queued it at
        // all, so a genuinely unsafe pair fell through both the automatic and manual paths.
        // Using the identical unfiltered query as Cleanup keeps the two services' view of
        // "does this pair overlap" in sync by construction, not by parallel maintenance.
        var allParentedForContainers = await db.MediaItems
            .Where(m => m.ParentId != null)
            .Select(m => new { m.Id, m.ParentId, m.MediaTypeId, m.HierarchyLevel, m.Number, m.IsStub })
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

        // Same-parent, same-Number CONTAINER pass -- see this class's own doc for the
        // 2026-08-03 incident this closes the gap for. Deliberately restricted to non-leaf
        // levels (a container's Number is its whole identity under its parent) so this can
        // never fire for the many legitimate same-Number leaf siblings that already exist in
        // this library (e.g. an unreliably-parsed reality show can have several genuinely
        // different episodes all parsed as "Number 5" -- see UpsertGroupItemAsync's own
        // "tertiary-and-a-half" tier doc for that confirmed case) and would otherwise flood
        // the review queue with false positives.
        //
        // Only reaches this human-review queue when the two containers' OWN children overlap
        // by Number (e.g. both have an "Episode 3") -- per-user request (2026-09-13): minimize
        // how often a person has to review a duplicate at all, not just make review easier.
        // A non-overlapping pair (e.g. one container holds episodes 1-10 and its duplicate
        // holds 11-20, or one is simply empty) is unambiguous -- DuplicateCleanupService's own
        // container-merge pass (see its Pass 5 doc) resolves those automatically and this pass
        // never sees them. An overlap is exactly the ambiguous case a plain reparent-based merge
        // can't safely resolve on its own (which of the two conflicting "Episode 3" rows is the
        // real one is a judgment call -- see the near-miss caught live 2026-09-13 merging real
        // Rick and Morty episodes into deletion), so it's the one shape still left for a human.
        void AddSameParentSameNumberCandidates()
        {
            var childNumbersByParent = allParentedForContainers
                .Where(m => m.ParentId is not null && m.Number is not null)
                .GroupBy(m => m.ParentId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(m => m.Number!.Value).ToHashSet());

            var containers = allParentedForContainers
                .Where(m => m.ParentId is not null && m.Number is not null
                         && hierarchyLevelsByType.TryGetValue(m.MediaTypeId, out var levels)
                         && m.HierarchyLevel < levels - 1)
                .ToDictionary(m => m.Id);

            var containerGroups = containers.Values
                .GroupBy(m => (m.ParentId, m.MediaTypeId, m.Number));

            foreach (var group in containerGroups)
            {
                var list = group.ToList();
                if (list.Count < 2) continue;
                for (int i = 0; i < list.Count - 1; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    childNumbersByParent.TryGetValue(list[i].Id, out var childrenI);
                    childNumbersByParent.TryGetValue(list[j].Id, out var childrenJ);
                    var overlaps = childrenI is not null && childrenJ is not null && childrenI.Overlaps(childrenJ);

                    // Mirrors DuplicateCleanupService's own Pass 5 safety check exactly (see its
                    // doc for the zero-children gap this closes) -- absence of overlap is only
                    // real evidence when we know what's actually in both containers. A container
                    // with no recorded children is safe to treat as a non-conflict only when it's
                    // a confirmed stub (IsStub); otherwise (a fully-scanned container that simply
                    // has no children yet) bare Number/Parent/Type agreement alone isn't enough to
                    // silently skip past, so this queues it for a human instead of trusting it.
                    var childrenIEmpty = childrenI is null || childrenI.Count == 0;
                    var childrenJEmpty = childrenJ is null || childrenJ.Count == 0;
                    var emptySideUnverified = (childrenIEmpty && !list[i].IsStub) || (childrenJEmpty && !list[j].IsStub);

                    if (!overlaps && !emptySideUnverified)
                        continue; // safe case -- DuplicateCleanupService auto-merges it

                    var a = Math.Min(list[i].Id, list[j].Id);
                    var b = Math.Max(list[i].Id, list[j].Id);
                    if (!dismissedSet.Contains((a, b)))
                        newCandidates.Add((a, b));
                }
            }
        }
        AddSameParentSameNumberCandidates();

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

        var parentById = items.ToDictionary(m => m.Id, m => m.ParentId);
        await LogShowPathMismatchesAsync(db, parentById, ct);
    }

    /// <summary>
    /// Read-only diagnostic pass: for every leaf item (episode/track/...) with a recorded
    /// physical file, checks whether that file's own folder path actually contains its root
    /// ancestor's Name (the "show" a TV episode nominally belongs to). A mismatch is the exact
    /// fingerprint left behind by the 2026-08-03 corruption incident (root-caused 2026-09-13):
    /// records fabricated for the wrong show, correctly named/numbered but carrying another
    /// show's file path in fileScanner.filePaths. Unlike the candidate passes above, a single
    /// mismatched item has no "other half" to pair it with, so it can't go through the
    /// pair-based Duplicates review queue -- this only ever logs a summary for a human to look
    /// at, exactly like every other pass here never deletes or merges anything itself.
    /// </summary>
    private async Task LogShowPathMismatchesAsync(
        ChronicleDbContext db,
        Dictionary<int, int?> parentById,
        CancellationToken ct)
    {
        int? RootIdOf(int id)
        {
            var current = id;
            var guard = 0; // defends against a corrupt/cyclical ParentId chain
            while (parentById.TryGetValue(current, out var parent) && parent is not null && guard++ < 64)
                current = parent.Value;
            return current;
        }

        var leafRows = await db.MediaItems
            .Where(m => m.MetadataJson != null && EF.Functions.Like(m.MetadataJson, "%fileScanner%"))
            .Select(m => new { m.Id, m.ParentId, m.MetadataJson })
            .ToListAsync(ct);

        var rootIds = leafRows.Select(r => RootIdOf(r.Id)).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var rootNames = await db.MediaItems
            .Where(m => rootIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Name })
            .ToDictionaryAsync(m => m.Id, m => m.Name, ct);

        static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        var mismatchesByShow = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in leafRows)
        {
            var rootId = RootIdOf(row.Id);
            if (rootId is null || !rootNames.TryGetValue(rootId.Value, out var showName) || string.IsNullOrEmpty(showName))
                continue;

            var paths = FileIdentityJson.ExtractFilePaths(row.MetadataJson);
            if (paths.Count == 0) continue;

            var showKey = Normalize(showName);
            var anyMatches = paths.Any(p => Normalize(p).Contains(showKey));
            if (!anyMatches)
            {
                if (!mismatchesByShow.TryGetValue(showName, out var list))
                    mismatchesByShow[showName] = list = new List<string>();
                list.Add(paths[0]);
            }
        }

        if (mismatchesByShow.Count > 0)
        {
            var totalMismatches = mismatchesByShow.Values.Sum(v => v.Count);
            logger.LogWarning(
                "DuplicateCandidateScan: {Count} item(s) across {ShowCount} show(s) have a recorded file " +
                "path that doesn't match their own show's name -- possible stale/corrupted fileScanner " +
                "data (see the 2026-08-03 incident this check was added for). Examples: {Examples}",
                totalMismatches, mismatchesByShow.Count,
                string.Join("; ", mismatchesByShow.Take(8).Select(kv => $"{kv.Key} ({kv.Value.Count}), e.g. '{kv.Value[0]}'")));
        }
    }
}
