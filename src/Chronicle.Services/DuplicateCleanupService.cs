using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Scheduled task that periodically scans for duplicate <see cref="MediaItem"/> records
/// (items that share the same physical file path in their <c>MetadataJson</c>) and removes all
/// but the best-quality copy.
///
/// "Best" is determined by a simple score: poster &gt; overview &gt; external IDs &gt; refresh logs.
/// Before a duplicate is removed its <see cref="UserLibrary"/> and <see cref="InteractionEvent"/>
/// rows are reassigned to the surviving item so no user data is lost.
/// </summary>
public sealed class DuplicateCleanupService : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _log = Log.ForContext<DuplicateCleanupService>();

    // Strips trailing "(YYYY)" / "[YYYY]" for title-normalisation comparisons in Pass 3.
    private static readonly Regex _yearTailRe = new(@"\s*[\(\[]\d{4}[\)\]]\s*$", RegexOptions.Compiled);

    public DuplicateCleanupService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // ── IScheduledTask ────────────────────────────────────────────────────────────
    public string TaskId      => "duplicate_cleanup";
    public string DisplayName => "Duplicate Cleanup";
    public string Description => "Scans for duplicate media items sharing the same file path and removes all but the best-quality copy.";
    public string DefaultCron => "0 3 * * *";

    async Task IScheduledTask.ExecuteAsync(CancellationToken ct)
    {
        var removed = await RunAsync(ct);
        if (removed > 0)
            _log.Information("DuplicateCleanup: removed {Count} duplicate media items", removed);
    }

    /// <summary>
    /// Scans the database for duplicate media items (matched by file path) and eliminates
    /// all but the highest-scored copy.  Returns the number of items removed.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context           = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var resolutionService = scope.ServiceProvider.GetRequiredService<IMetadataResolutionService>();

        int removed = 0;
        var alreadyRemoved = new HashSet<int>();

        // ── Pass 1: file-path duplicates ──────────────────────────────────────
        // Items that share the same physical file path in fileScanner.filePaths[0].
        // NOTE: folderPath is NOT used here — it is the parent directory and is shared
        // by every item in a folder, which would incorrectly flag season episodes.
        var itemsWithPaths = await context.MediaItems
            .Include(m => m.ExternalIds)
            .Where(m => m.MetadataJson != null
                     && EF.Functions.Like(m.MetadataJson, "%fileScanner%"))
            .ToListAsync(ct);

        var filePathGroups = itemsWithPaths
            .GroupBy(m => FileIdentityJson.PrimaryFilePathKey(m.MetadataJson) ?? string.Empty,
                     StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1)
            .ToList();

        if (filePathGroups.Count > 0)
        {
            _log.Information("DuplicateCleanup: found {Count} file path(s) with duplicate items",
                filePathGroups.Count);

            foreach (var group in filePathGroups)
            {
                ct.ThrowIfCancellationRequested();

                // Never auto-merge across media types. Two items can end up resolving to the
                // same file-path key across types either from stale/legacy data or from an
                // upstream scan-matching bug — either way, silently collapsing a Fan Edit into
                // its source Movie (or vice versa) destroys real user data (the item's own type,
                // its own identity). Split by type and only dedupe within a type; anything left
                // spanning multiple types is surfaced in the log for manual review/unmerge.
                var byType = group.GroupBy(m => m.MediaTypeId).ToList();
                if (byType.Count > 1)
                {
                    _log.Warning(
                        "DuplicateCleanup: path '{Path}' resolves to items of {TypeCount} different media " +
                        "types ({Items}) — skipping auto-merge across types; review manually.",
                        group.Key, byType.Count,
                        string.Join(", ", group.Select(m => $"{m.Id}:{m.Name}(type={m.MediaTypeId})")));
                }

                foreach (var typeGroup in byType.Where(t => t.Count() > 1))
                {
                    // Oldest record wins — preserves the item the user has been tracking longest.
                    var ordered = typeGroup.OrderBy(m => m.Id).ThenByDescending(ScoreItem).ToList();
                    var winner = ordered[0];
                    foreach (var loser in ordered.Skip(1))
                    {
                        _log.Information(
                            "DuplicateCleanup: path '{Path}' — keeping {WId} ('{WName}'), removing {LId} ('{LName}')",
                            group.Key, winner.Id, winner.Name, loser.Id, loser.Name);
                        await using var tx = await context.Database.BeginTransactionAsync(ct);
                        var merged = await MergeAndDeleteAsync(context, resolutionService, winner, loser, ct);
                        await context.SaveChangesAsync(ct);
                        await tx.CommitAsync(ct);
                        if (merged)
                        {
                            alreadyRemoved.Add(loser.Id);
                            removed++;
                        }
                    }
                }
            }
        }

        // ── Pass 2: external-ID duplicates ────────────────────────────────────
        // Catches items imported by two different sources (e.g. file scanner + SIMKL sync)
        // that share the same (source, externalId) entry in media_external_ids.
        // These never share a file path, so Pass 1 misses them entirely.
        var allExternalIds = await context.MediaExternalIds
            .Include(e => e.MediaItem).ThenInclude(m => m!.ExternalIds)
            .Where(e => e.MediaItem != null)
            .ToListAsync(ct);

        var extIdGroups = allExternalIds
            .GroupBy(e => $"{e.Source}:{e.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(e => e.MediaItemId).Distinct().Count() > 1)
            // "__suppress__" is a sentinel meaning "don't enrich from this plugin" — it is NOT
            // a real external ID and must never be used as a duplicate-matching key.
            .Where(g => !g.Key.EndsWith(":__suppress__", StringComparison.OrdinalIgnoreCase))
            // Confirmed root cause (2026-08-05): "chronicle:manual-collection-member" is a
            // sentinel (MovieCollectionService.ManualCollectionMemberMarker) written onto EVERY
            // movie the user has ever explicitly placed into a collection via
            // ReparentIntoCollectionAsync — it is the exact same literal string for every such
            // item, regardless of which collection or how unrelated the movies actually are.
            // Treating it as a duplicate-matching key collapsed every collection-member movie in
            // the entire library into one arbitrary winner in a single run (Rogue One, Solo,
            // Dungeons & Dragons, Fast & Furious 10, and more all merged into an unrelated
            // Metallica concert film). Like "__suppress__", this is bookkeeping, not identity.
            .Where(g => !g.Key.EndsWith(":manual-collection-member", StringComparison.OrdinalIgnoreCase))
            // Confirmed root cause (2026-08-05): Chronicle.Plugin.FanartTV's own documented
            // behavior legitimately returns "artist:{mbid}" for BOTH a music artist's own root
            // item AND as a fallback for any child album whose own release-group MBID isn't
            // resolvable (FanartTvMetadataProvider.ResolveExternalId, level-1 branch — "If we
            // only have the artist MBID... fall back to artist-level artwork"). Every album by
            // the same artist that hits that fallback shares the identical string, so this
            // (source, externalId) pair is NOT unique-per-item by design and must never be used
            // as a merge signal — doing so once collapsed 35 distinct albums into their shared
            // artist item in a single run. Unlike every other source Pass 2 checks (tmdb/imdb/
            // tvdb/simkl/etc, which genuinely are 1:1 with a real-world item), Fanart.tv's own
            // "fanarttv" source is deliberately excluded here rather than fixed at the plugin
            // level, since nulling the plugin's returned ExternalId would introduce a null into
            // MetadataEnrichmentService.UpsertExternalIdForEnrichmentAsync's unguarded
            // rawExternalId.StartsWith(...) call — a NullReferenceException in a path every
            // plugin shares — for a much larger blast radius than skipping this one source here.
            .Where(g => !g.Key.StartsWith("fanarttv:artist:", StringComparison.OrdinalIgnoreCase))
            // Confirmed root cause (2026-08-05): same bug class as fanarttv:artist: above, this
            // time in Chronicle.Plugin.Hardcover. HardcoverMetadataProvider's series-search path
            // returns "hardcover:series:{id}" as a fallback ExternalId whenever an individual
            // book/edition can't be individually disambiguated — every sibling volume in that
            // series that hits the same fallback gets the identical string written onto its own
            // media_external_ids row via MetadataEnrichmentService.UpsertExternalIdForEnrichmentAsync,
            // so it is NOT unique-per-item. Confirmed live in the DB: 20+ distinct hardcover:series:
            // values were each shared across 2-3 unrelated MediaItems at time of writing. This is
            // the exact mechanism suspected (though not conclusively isolated) in an incident where
            // nine genuinely different-year "Alice in Borderland" volumes collapsed into one.
            .Where(g => !g.Key.StartsWith("hardcover:hardcover:series:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (extIdGroups.Count > 0)
        {
            _log.Information("DuplicateCleanup: found {Count} external ID(s) shared by multiple items",
                extIdGroups.Count);

            foreach (var group in extIdGroups)
            {
                ct.ThrowIfCancellationRequested();

                var items = group
                    .Select(e => e.MediaItem)
                    .Where(m => m is not null)          // guard: Pass 1 may have deleted some items
                    .Select(m => m!)
                    .DistinctBy(m => m.Id)
                    .Where(m => !alreadyRemoved.Contains(m.Id))
                    .ToList();

                if (items.Count < 2) continue;

                // Skip groups where items span different media types — a fan edit and the
                // source movie intentionally share the same TMDB/IMDB external ID but are
                // distinct items that must not be merged.
                if (items.Select(m => m.MediaTypeId).Distinct().Count() > 1) continue;

                // Prefer the item with a physical file (fileScanner in MetadataJson).
                // Among equal candidates, highest score then lowest Id wins.
                var ordered = items
                    .OrderByDescending(m => m.MetadataJson != null && m.MetadataJson.Contains("\"fileScanner\""))
                    .ThenByDescending(ScoreItem)
                    .ThenBy(m => m.Id)
                    .ToList();

                var winner = ordered[0];
                foreach (var loser in ordered.Skip(1))
                {
                    _log.Information(
                        "DuplicateCleanup: external ID '{Key}' — keeping {WId} ('{WName}'), removing {LId} ('{LName}')",
                        group.Key, winner.Id, winner.Name, loser.Id, loser.Name);
                    await using var tx = await context.Database.BeginTransactionAsync(ct);
                    var merged = await MergeAndDeleteAsync(context, resolutionService, winner, loser, ct);
                    await context.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    if (merged)
                    {
                        alreadyRemoved.Add(loser.Id);
                        removed++;
                    }
                }
            }
        }

        // ── Pass 3: title-normalisation duplicates ─────────────────────────────
        // Catches file-scanner items like "Batman v Superman - Dawn of Justice (2016)"
        // that duplicated SIMKL stubs like "Batman v Superman: Dawn of Justice (2016)"
        // because Windows filenames cannot contain colons.
        // Groups root-level items by (normalised title, year, media type).
        // Use a projection to avoid loading full entity graph for every root item.
        var rootProjections = await context.MediaItems
            .Where(m => m.HierarchyLevel == 0)
            .Select(m => new { m.Id, m.Name, m.Year, m.MediaTypeId, m.MetadataJson })
            .ToListAsync(ct);

        var titleGroups = rootProjections
            .Where(m => !alreadyRemoved.Contains(m.Id))
            .GroupBy(m => (NormalizeTitle(m.Name), m.Year, m.MediaTypeId))
            .Where(g => g.Count() > 1)
            // Only merge a file-scanned item with a sync stub — require one of each.
            // This avoids false-positives when two TMDB-only items happen to share a name.
            .Where(g =>
                g.Any(m  => m.MetadataJson != null && m.MetadataJson.Contains("\"fileScanner\"")) &&
                g.Any(m  => m.MetadataJson == null  || !m.MetadataJson.Contains("\"fileScanner\"")))
            .ToList();

        if (titleGroups.Count > 0)
        {
            _log.Information(
                "DuplicateCleanup: found {Count} title-normalised group(s) with duplicate items",
                titleGroups.Count);

            foreach (var group in titleGroups)
            {
                ct.ThrowIfCancellationRequested();
                var projections = group.Where(m => !alreadyRemoved.Contains(m.Id)).ToList();
                if (projections.Count < 2) continue;

                // File-scanner items preferred (they have physical files).
                var orderedProjections = projections
                    .OrderByDescending(m => m.MetadataJson != null && m.MetadataJson.Contains("\"fileScanner\""))
                    .ThenByDescending(m => ScoreProjection(m.MetadataJson))
                    .ThenBy(m => m.Id)
                    .ToList();

                // Load full MediaItem entities only for the small set involved in this merge.
                var groupIds = orderedProjections.Select(m => m.Id).ToList();
                var itemsById = await context.MediaItems
                    .Where(m => groupIds.Contains(m.Id))
                    .ToDictionaryAsync(m => m.Id, ct);

                var winner = itemsById[orderedProjections[0].Id];
                foreach (var loserProjection in orderedProjections.Skip(1))
                {
                    if (!itemsById.TryGetValue(loserProjection.Id, out var loser)) continue;
                    _log.Information(
                        "DuplicateCleanup: title-match '{Key}' — keeping {WId} ('{WName}'), removing {LId} ('{LName}')",
                        $"{group.Key.Item1} / {group.Key.Year}", winner.Id, winner.Name, loser.Id, loser.Name);
                    await using var tx = await context.Database.BeginTransactionAsync(ct);
                    var merged = await MergeAndDeleteAsync(context, resolutionService, winner, loser, ct);
                    await context.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    if (merged)
                    {
                        alreadyRemoved.Add(loser.Id);
                        removed++;
                    }
                }
            }
        }

        // ── Pass 4: same-parent, same-name duplicates ──────────────────────────
        // Catches duplicates that Passes 1-3 all miss: items restored via Unmerge (see
        // MergeService.UnmergeAsync) never get their Year or Number back — the merge log
        // never captured them in the first place — so a restored duplicate has Year=null
        // while its sibling has the real year, and Pass 3's exact-year grouping key never
        // matches them.
        //
        // CONFIRMED FALSE (2026-08-05): this pass previously assumed "a collection's member
        // list is deduped by external ID upstream, so it would never legitimately contain two
        // distinct entries under one parent with an identical title" and merged on name+parent
        // alone. Seven genuinely different volumes/editions of "Alice in Borderland" (Year 2012/
        // 2013/2014, each with its own distinct hardcover external ID) shared a parent and title
        // and were incorrectly collapsed into one in a single run. The Year/ExternalId guards
        // below close that hole the same way Pass 3 already requires exact-Year agreement.
        var parentedProjections = await context.MediaItems
            .Where(m => m.ParentId != null)
            .Select(m => new { m.Id, m.Name, m.ParentId, m.MediaTypeId, m.Number, m.Year, m.MetadataJson })
            .ToListAsync(ct);

        var nameGroups = parentedProjections
            .Where(m => !alreadyRemoved.Contains(m.Id))
            .GroupBy(m => (m.ParentId, NormalizeTitle(m.Name), m.MediaTypeId))
            .Where(g => g.Count() > 1)
            .ToList();

        if (nameGroups.Count > 0)
        {
            _log.Information(
                "DuplicateCleanup: found {Count} same-parent name-matched group(s) with duplicate items",
                nameGroups.Count);

            foreach (var group in nameGroups)
            {
                ct.ThrowIfCancellationRequested();
                var projections = group.Where(m => !alreadyRemoved.Contains(m.Id)).ToList();
                if (projections.Count < 2) continue;

                var orderedProjections = projections
                    .OrderByDescending(m => m.MetadataJson != null && m.MetadataJson.Contains("\"fileScanner\""))
                    .ThenByDescending(m => ScoreProjection(m.MetadataJson))
                    .ThenBy(m => m.Id)
                    .ToList();

                var groupIds = orderedProjections.Select(m => m.Id).ToList();
                var itemsById = await context.MediaItems
                    .Include(m => m.ExternalIds)
                    .Where(m => groupIds.Contains(m.Id))
                    .ToDictionaryAsync(m => m.Id, ct);

                var winner = itemsById[orderedProjections[0].Id];
                foreach (var loserProjection in orderedProjections.Skip(1))
                {
                    if (!itemsById.TryGetValue(loserProjection.Id, out var loser)) continue;

                    // Guard: two DIFFERENT numbered siblings under the same parent (e.g. two
                    // distinct tracks/episodes that happen to share a generic title) are not
                    // duplicates. Only treat as a dup when at least one side's Number is unset.
                    if (winner.Number.HasValue && loser.Number.HasValue && winner.Number != loser.Number)
                        continue;

                    // Guard: two DIFFERENT years (e.g. distinct editions/printings of the same
                    // title) are not duplicates. Mirrors the Number guard above and Pass 3's
                    // exact-Year requirement — only treat as a dup when at least one side's
                    // Year is unset (can't disprove) or both agree.
                    if (winner.Year.HasValue && loser.Year.HasValue && winner.Year != loser.Year)
                        continue;

                    // Guard: if both sides carry an external ID from the same source but with
                    // different values (e.g. two different hardcover.app edition IDs), that is
                    // direct evidence they are distinct real-world items, not duplicates —
                    // regardless of what their names/parent happen to share.
                    var winnerIdsBySource = winner.ExternalIds.ToLookup(e => e.Source, e => e.ExternalId, StringComparer.OrdinalIgnoreCase);
                    var hasConflictingExternalId = loser.ExternalIds.Any(le =>
                        winnerIdsBySource[le.Source].Any(we => !string.Equals(we, le.ExternalId, StringComparison.OrdinalIgnoreCase)));
                    if (hasConflictingExternalId)
                        continue;

                    _log.Information(
                        "DuplicateCleanup: same-parent name-match '{Key}' — keeping {WId} ('{WName}'), removing {LId} ('{LName}')",
                        $"{group.Key.Item2} / parent {group.Key.ParentId}", winner.Id, winner.Name, loser.Id, loser.Name);
                    await using var tx = await context.Database.BeginTransactionAsync(ct);
                    var merged = await MergeAndDeleteAsync(context, resolutionService, winner, loser, ct);
                    await context.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    if (merged)
                    {
                        alreadyRemoved.Add(loser.Id);
                        removed++;
                    }
                }
            }
        }

        if (removed > 0)
            _log.Information("DuplicateCleanup: total {Count} duplicate item(s) removed", removed);

        return removed;
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reassigns all user data from <paramref name="loser"/> to <paramref name="winner"/>
    /// then marks <paramref name="loser"/> for deletion.
    /// Changes are staged on <paramref name="context"/>; the caller issues SaveChangesAsync.
    /// Returns false (and stages no changes) if a structural invariant is violated — the
    /// caller must NOT treat the pair as merged in that case.
    /// </summary>
    private static async Task<bool> MergeAndDeleteAsync(
        ChronicleDbContext context,
        IMetadataResolutionService resolutionService,
        MediaItem winner,
        MediaItem loser,
        CancellationToken ct)
    {
        // ── Structural invariant guards ────────────────────────────────────────────
        // Mirrors MergeService.MergeAsync's guards, which this method duplicates rather than
        // calls (different transaction/batch shape). Every automatic pass above filters loosely
        // (by file path, external ID, or name) before reaching here — these are the same hard
        // safety checks the manual merge path enforces, so an automatic pass can never merge
        // across hierarchy levels or across parents the way a data-quality edge case could sneak
        // a shared file-path/external-ID/name match past the looser upstream filters.
        if (winner.HierarchyLevel != loser.HierarchyLevel)
        {
            Log.ForContext<DuplicateCleanupService>().Warning(
                "MergeAndDelete: skipping {WId}/{LId} — hierarchy levels differ ({WLevel} vs {LLevel})",
                winner.Id, loser.Id, winner.HierarchyLevel, loser.HierarchyLevel);
            return false;
        }
        if (winner.HierarchyLevel > 0 && winner.ParentId != loser.ParentId)
        {
            Log.ForContext<DuplicateCleanupService>().Warning(
                "MergeAndDelete: skipping {WId}/{LId} — non-root items with different parents ({WParent} vs {LParent})",
                winner.Id, loser.Id, winner.ParentId, loser.ParentId);
            return false;
        }

        // ── Snapshot loser state BEFORE any re-pointing (for merge log) ──────────
        // These queries MUST run first — once ExternalIds and Children are re-parented
        // to the winner, querying by loser.Id returns nothing.
        var loserExtIdsRaw = await context.MediaExternalIds
            .Where(e => e.MediaItemId == loser.Id)
            .Select(e => new { e.Source, e.ExternalId })
            .ToListAsync(ct);
        // Load full child entities here — re-used for both the snapshot IDs and re-parenting,
        // avoiding a second query later.
        var loserChildrenSnapshot = await context.MediaItems
            .Where(m => m.ParentId == loser.Id)
            .ToListAsync(ct);
        var loserChildIdsSnapshot = loserChildrenSnapshot.Select(m => m.Id).ToList();

        // Queried fresh rather than read off winner.ExternalIds — several of the callers above
        // (Pass 3/Pass 4's itemsById lookups) load `winner` without .Include(ExternalIds), and
        // with no lazy-loading proxies configured, the nav collection would silently read as
        // empty for those, causing every duplicate-detection check below to falsely conclude
        // the winner owns none of its own external IDs.
        var winnerExtIdsSnapshot = await context.MediaExternalIds
            .Where(e => e.MediaItemId == winner.Id)
            .Select(e => new { e.Source, e.ExternalId })
            .ToListAsync(ct);

        // Same distinction MergeService.MergeAsync records: whether the winner already owned
        // an identical (Source, ExternalId) row before this merge. Unmerge needs this to avoid
        // stealing the winner's own pre-merge external ID when restoring the loser as a stub —
        // see MergeService.UnmergeAsync's LoserExternalId.WasDuplicate handling.
        var winnerIdSetForLog = winnerExtIdsSnapshot
            .Select(e => $"{e.Source}:{e.ExternalId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var loserExtIdsSnapshot = loserExtIdsRaw
            .Select(e => new LoserExternalIdSnapshot(
                e.Source, e.ExternalId, winnerIdSetForLog.Contains($"{e.Source}:{e.ExternalId}")))
            .ToList();

        // Repoint merge-log rows where the loser was itself a previous merge's winner — see
        // MergeService.MergeAsync for the full rationale (deleting the loser below would
        // otherwise cascade-delete that earlier merge's audit trail via the MediaItemMerges
        // .WinnerId FK, permanently breaking Unmerge for it).
        var priorMergesWonByLoser = await context.MediaItemMerges
            .Where(m => m.WinnerId == loser.Id)
            .ToListAsync(ct);
        foreach (var priorMerge in priorMergesWonByLoser)
            priorMerge.WinnerId = winner.Id;

        // ── UserLibrary ───────────────────────────────────────────────────────────
        // For each user who has the loser in their library: if the winner is already
        // there, merge — keeping the better status (e.g. Completed > Unwatched).
        var loserLibEntries = await context.UserLibraries
            .Where(l => l.MediaItemId == loser.Id)
            .ToListAsync(ct);
        var loserLibUserIds = loserLibEntries.Select(l => l.UserId).ToList();
        var winnerLibByUser = loserLibUserIds.Count > 0
            ? await context.UserLibraries
                .Where(l => l.MediaItemId == winner.Id && loserLibUserIds.Contains(l.UserId))
                .ToDictionaryAsync(l => l.UserId, ct)
            : new Dictionary<int, UserLibrary>();

        foreach (var lib in loserLibEntries)
        {
            winnerLibByUser.TryGetValue(lib.UserId, out var winnerLib);

            if (winnerLib is not null)
            {
                var loserRank  = StatusRank(lib.Status);
                var winnerRank = StatusRank(winnerLib.Status);
                if (loserRank > winnerRank)
                {
                    winnerLib.Status      = lib.Status;
                    winnerLib.CompletedAt = lib.CompletedAt ?? winnerLib.CompletedAt;
                    winnerLib.UserRating  = lib.UserRating  ?? winnerLib.UserRating;
                    winnerLib.UpdatedAt   = DateTime.UtcNow;
                }
                else
                {
                    // Same or lower rank — keep winner's status but fill any missing data.
                    winnerLib.CompletedAt ??= lib.CompletedAt;
                    winnerLib.UserRating  ??= lib.UserRating;
                    winnerLib.UpdatedAt = DateTime.UtcNow;
                }
                context.UserLibraries.Remove(lib);
            }
            else
                lib.MediaItemId = winner.Id;
        }

        // ── InteractionEvents ─────────────────────────────────────────────────────
        // Re-point loser events to the winner, but deduplicate first: if the winner
        // already has an event for the same (UserId, Timestamp) the UNIQUE constraint
        // would be violated. Drop the loser's copy in that case — the data is identical.
        var loserEvents = await context.InteractionEvents
            .Where(e => e.MediaItemId == loser.Id)
            .ToListAsync(ct);

        if (loserEvents.Count > 0)
        {
            // Build a set of (UserId, Timestamp) pairs already on the winner.
            var winnerEventKeys = (await context.InteractionEvents
                .Where(e => e.MediaItemId == winner.Id)
                .Select(e => new { e.UserId, e.Timestamp })
                .ToListAsync(ct))
                .Select(e => new UserTimestampKey(e.UserId, e.Timestamp))
                .ToHashSet(new UserTimestampComparer());

            foreach (var ev in loserEvents)
            {
                if (winnerEventKeys.Contains(new UserTimestampKey(ev.UserId, ev.Timestamp)))
                    context.InteractionEvents.Remove(ev);   // duplicate — discard
                else
                    ev.MediaItemId = winner.Id;             // unique — re-point
            }
        }

        // ── MediaListItems ────────────────────────────────────────────────────────
        var loserListItems = await context.MediaListItems
            .Where(li => li.MediaItemId == loser.Id)
            .ToListAsync(ct);

        foreach (var li in loserListItems)
            li.MediaItemId = winner.Id;

        // ── MediaCredits — re-point; deduplicate by (person_name, role) ─────────────
        var loserCredits = await context.MediaCredits
            .Where(c => c.MediaItemId == loser.Id)
            .ToListAsync(ct);
        var winnerCreditSet = (await context.MediaCredits
            .Where(c => c.MediaItemId == winner.Id)
            .Select(c => new { c.PersonName, c.Role })
            .ToListAsync(ct))
            .Select(c => $"{c.PersonName}\0{c.Role}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var credit in loserCredits)
        {
            if (winnerCreditSet.Contains($"{credit.PersonName}\0{credit.Role}"))
                context.MediaCredits.Remove(credit);
            else
                credit.MediaItemId = winner.Id;
        }

        // ── Child media items — re-parent to winner ───────────────────────────────
        // Use the snapshot already loaded at the top rather than querying again.
        foreach (var child in loserChildrenSnapshot)
        {
            child.ParentId = winner.Id;
            child.NormalizedName = MediaItemNormalizer.NormalizeName(child.Name);
        }

        // ── MediaExternalIds — merge into winner, don't just delete ──────────────
        // Grafting the loser's IDs (e.g. "simkl:12345") onto the winner means
        // future syncs resolve at Stage 1 without re-creating the stub.
        var winnerIdSet = winnerIdSetForLog;

        var loserExternalIds = await context.MediaExternalIds
            .Where(e => e.MediaItemId == loser.Id)
            .ToListAsync(ct);

        foreach (var ext in loserExternalIds)
        {
            if (winnerIdSet.Contains($"{ext.Source}:{ext.ExternalId}"))
                context.MediaExternalIds.Remove(ext);   // winner already has it — drop duplicate row
            else
                ext.MediaItemId = winner.Id;            // graft onto winner
        }

        // ── MediaEnrichments — remove loser's rows; reset winner rows for new sources ──
        context.MediaEnrichments.RemoveRange(
            await context.MediaEnrichments.Where(e => e.MediaItemId == loser.Id).ToListAsync(ct));

        // Reset winner enrichment rows for sources that were newly grafted (not deleted as
        // duplicates) so the winner is re-enriched with the combined external IDs.
        var newSources = loserExternalIds
            .Where(e => !winnerIdSet.Contains($"{e.Source}:{e.ExternalId}"))
            .Select(e => e.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (newSources.Count > 0)
        {
            var winnerEnrichmentRows = await context.MediaEnrichments
                .Where(e => e.MediaItemId == winner.Id)
                .ToListAsync(ct);
            foreach (var row in winnerEnrichmentRows)
            {
                var shortId = PluginIdHelper.ToSource(row.PluginId);
                if (newSources.Contains(shortId) &&
                    row.Status is Chronicle.Core.Models.EnrichmentStatus.Completed
                               or Chronicle.Core.Models.EnrichmentStatus.NotFound
                               or Chronicle.Core.Models.EnrichmentStatus.Exhausted)
                {
                    row.Status     = Chronicle.Core.Models.EnrichmentStatus.Pending;
                    row.RetryCount = 0;
                    row.ErrorMessage = null;
                }
            }
        }

        // ── AKA ───────────────────────────────────────────────────────────────────
        // Skip episode-pattern names (e.g. "Show S01E03 - Title") — they are child items,
        // not real alternate titles, and would pollute the AKA line on the parent. Mirrors
        // MergeService.MergeAsync's identical guard.
        if (MergeService.NamesRequireAka(winner.Name, loser.Name)
            && !Regex.IsMatch(loser.Name, @"S\d{1,2}E\d{1,2}", RegexOptions.IgnoreCase))
        {
            context.MediaItemAliases.Add(new Chronicle.Core.Models.MediaItemAlias
            {
                MediaItemId = winner.Id,
                Alias       = loser.Name,
                Source      = "merge",
                CreatedAt   = DateTime.UtcNow,
            });
        }

        // ── Remove the stale (winner, loser) duplicate-candidate row, if any ──────
        // Rows referencing only the loser are already handled by that FK's cascade delete
        // below; this covers the specific winner/loser pair. Mirrors MergeService.MergeAsync.
        var staleCandidates = await context.MediaItemDuplicateCandidates
            .Where(c => (c.ItemAId == winner.Id || c.ItemAId == loser.Id) &&
                        (c.ItemBId == winner.Id || c.ItemBId == loser.Id))
            .ToListAsync(ct);
        context.MediaItemDuplicateCandidates.RemoveRange(staleCandidates);

        // ── Record merge log (enables unmerge) ───────────────────────────────────
        // Uses the snapshot captured at the top, before any re-pointing occurred.
        context.MediaItemMerges.Add(new Chronicle.Core.Models.MediaItemMerge
        {
            WinnerId             = winner.Id,
            LoserOriginalId      = loser.Id,
            LoserName            = loser.Name,
            LoserMediaTypeId     = loser.MediaTypeId,
            LoserHierarchyLevel  = loser.HierarchyLevel,
            LoserParentId        = loser.ParentId,
            LoserYear            = loser.Year,
            LoserNumber          = loser.Number,
            LoserExternalIdsJson = System.Text.Json.JsonSerializer.Serialize(loserExtIdsSnapshot),
            LoserChildIdsJson    = System.Text.Json.JsonSerializer.Serialize(loserChildIdsSnapshot),
            LoserMetadataJson    = loser.MetadataJson,
            MergedAt             = DateTime.UtcNow,
            MergedByUserId       = null, // automatic
        });

        // ── NormalizedName on winner ────────────────────────────────────────────
        winner.NormalizedName = MediaItemNormalizer.NormalizeName(winner.Name);

        // ── metadata_json — merge loser blobs into winner (winner takes precedence) ──
        // Ensures lossless ingestion: plugin data from the loser that the winner lacks
        // is preserved rather than discarded.
        if (!string.IsNullOrEmpty(loser.MetadataJson))
        {
            try
            {
                var winnerBlobs = ParseBlobs(winner.MetadataJson);
                var loserBlobs  = ParseBlobs(loser.MetadataJson);
                foreach (var (key, val) in loserBlobs)
                    if (!winnerBlobs.ContainsKey(key) && key != "_resolved")
                        winnerBlobs[key] = val;
                winner.MetadataJson = System.Text.Json.JsonSerializer.Serialize(winnerBlobs);
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed metadata — skip blob merge rather than aborting the cleanup.
            }
        }

        // ── Recompute _resolved so the UI reflects the merged metadata ────────────
        await resolutionService.ResolveAsync(winner, context, ct);

        // ── Stamp winner as modified ──────────────────────────────────────────────
        winner.UpdatedAt = DateTime.UtcNow;

        // ── Finally delete the loser ──────────────────────────────────────────────
        context.MediaItems.Remove(loser);
        return true;
    }

    /// <summary>
    /// Scores a media item for survivor selection.
    /// Higher is better.  Items with more metadata are preferred.
    /// </summary>
    private static int StatusRank(Chronicle.Core.Models.LibraryStatus status) => status switch
    {
        Chronicle.Core.Models.LibraryStatus.Rewatching  => 7,
        Chronicle.Core.Models.LibraryStatus.Completed   => 6,
        Chronicle.Core.Models.LibraryStatus.Watching    => 5,
        Chronicle.Core.Models.LibraryStatus.OnHold      => 4,
        Chronicle.Core.Models.LibraryStatus.PlanToWatch => 3,
        Chronicle.Core.Models.LibraryStatus.Dropped     => 2,
        _                                                => 1,  // Unwatched
    };

    private static string NormalizeTitle(string name)
    {
        // Strip trailing "(YYYY)" or "[YYYY]" then normalise " - " → ": " for comparison.
        var stripped = _yearTailRe.Replace(name, "").Trim();
        return stripped.Replace(" - ", ": ").ToLowerInvariant();
    }

    private static int ScoreItem(MediaItem item)
    {
        var score = 0;
        if (!string.IsNullOrEmpty(item.PosterUrl))  score += 40;
        if (!string.IsNullOrEmpty(item.Overview))   score += 30;
        if (item.ExternalIds.Any())                 score += 20;
        return score;
    }

    /// <summary>
    /// Lighter scoring for anonymous projections in Pass 3 where only MetadataJson is available.
    /// Cannot check ExternalIds (no nav property), so that 20-point criterion is omitted.
    /// </summary>
    private static int ScoreProjection(string? metadataJson)
    {
        if (metadataJson is null) return 0;
        var score = 0;
        // A "fileScanner" block in metadata suggests the item has more real data.
        if (metadataJson.Contains("\"fileScanner\"", StringComparison.OrdinalIgnoreCase)) score += 10;
        return score;
    }

    private static Dictionary<string, System.Text.Json.JsonElement> ParseBlobs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    // Shape matches MergeService's private LoserExternalId record exactly (same property names,
    // Source/ExternalId/WasDuplicate) — both write to and are read from the same
    // MediaItemMerges.LoserExternalIdsJson column via MergeService.UnmergeAsync, which is the
    // single shared unmerge path for merges recorded by either implementation.
    private record LoserExternalIdSnapshot(string Source, string ExternalId, bool WasDuplicate);

    // ── InteractionEvent deduplication helpers ────────────────────────────────

    private record UserTimestampKey(int UserId, DateTime Timestamp);

    private sealed class UserTimestampComparer
        : IEqualityComparer<UserTimestampKey>
    {
        public bool Equals(UserTimestampKey? x, UserTimestampKey? y) =>
            x is not null && y is not null
            && x.UserId == y.UserId
            && x.Timestamp == y.Timestamp;

        public int GetHashCode(UserTimestampKey obj) =>
            HashCode.Combine(obj.UserId, obj.Timestamp);
    }
}
