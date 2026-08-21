using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class MergeService(
    ChronicleDbContext db,
    IMetadataResolutionService resolutionService,
    IMovieCollectionService movieCollectionService,
    ILogger<MergeService> logger) : IMergeService
{
    public async Task MergeAsync(int winnerId, int loserId, int? mergedByUserId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // ── Guard checks ──────────────────────────────────────────────────────
        if (winnerId == loserId)
            throw new InvalidOperationException("Winner and loser must be different items.");

        var winner = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.Aliases)
            .FirstOrDefaultAsync(m => m.Id == winnerId, ct)
            ?? throw new InvalidOperationException($"Winner item {winnerId} not found.");

        var loser = await db.MediaItems
            .Include(m => m.MediaType)
            .FirstOrDefaultAsync(m => m.Id == loserId, ct)
            ?? throw new InvalidOperationException($"Loser item {loserId} not found.");

        // Reject if either item is referenced as a loser_original_id (already deleted)
        var alreadyMerged = await db.MediaItemMerges
            .AnyAsync(m => m.LoserOriginalId == winnerId || m.LoserOriginalId == loserId, ct);
        if (alreadyMerged)
            throw new InvalidOperationException("One of these items has already been merged and deleted.");

        var ineligibleReason = await CheckMergeEligibilityAsync(db, winner, loser, ct);
        if (ineligibleReason is not null)
            throw new InvalidOperationException(ineligibleReason);

        // If the media types differ, silently coerce the loser to the winner's type.
        // The loser is absorbed and deleted, so its type only matters for the merge log —
        // the resulting item will always carry the winner's type.
        if (winner.MediaTypeId != loser.MediaTypeId)
            loser.MediaTypeId = winner.MediaTypeId; // committed inside the transaction below

        await MergeLoadedItemsAsync(db, winner, loser, mergedByUserId, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Merged item {LoserId} ({LoserName}) into {WinnerId} ({WinnerName})",
            loserId, loser.Name, winnerId, winner.Name);
    }

    public async Task<string?> CheckMergeEligibilityAsync(
        ChronicleDbContext dbContext, MediaItem winner, MediaItem loser, CancellationToken ct = default)
    {
        // Non-root items (seasons, episodes, tracks) must share the same parent — merging
        // a season from one show into a season from another would corrupt the hierarchy.
        if (winner.HierarchyLevel != loser.HierarchyLevel)
            return $"hierarchy levels differ ({winner.HierarchyLevel} vs {loser.HierarchyLevel})";
        if (winner.HierarchyLevel > 0 && winner.ParentId != loser.ParentId)
            return $"non-root items must share the same parent item to be merged (winner parent " +
                   $"{winner.ParentId?.ToString() ?? "root"}, loser parent {loser.ParentId?.ToString() ?? "root"})";

        // A collection container carries real children, curated collection artwork, and a
        // "collection:{id}" identity marker -- none of which a plain movie merge was ever
        // designed to absorb or be absorbed into. Merging one into the other would either
        // destroy the container (as the loser, its identity/children scatter onto an unrelated
        // item) or corrupt an unrelated item into looking like a container (as the winner, it
        // inherits the loser's own "collection:{id}" ExternalId via the generic ID-migration
        // below). Two containers merging (the same collection matched under two different
        // sources) is fine and expected -- only a container/non-container MISMATCH is rejected.
        var winnerIsCollection = await movieCollectionService.IsCollectionContainerAsync(dbContext, winner.Id, ct);
        var loserIsCollection  = await movieCollectionService.IsCollectionContainerAsync(dbContext, loser.Id, ct);
        if (winnerIsCollection != loserIsCollection)
            return $"one item is a collection container and the other is not (winner={winnerIsCollection}, " +
                   $"loser={loserIsCollection}) -- merging would transplant or destroy collection identity/membership";

        return null;
    }

    public async Task MergeLoadedItemsAsync(
        ChronicleDbContext dbContext, MediaItem winner, MediaItem loser, int? mergedByUserId, CancellationToken ct = default)
    {
        var winnerId = winner.Id;
        var loserId  = loser.Id;

        // ── Snapshot for merge log ─────────────────────────────────────────────
        var loserExternalIds = await dbContext.MediaExternalIds
            .Where(e => e.MediaItemId == loserId)
            .ToListAsync(ct);

        var loserChildren = await dbContext.MediaItems
            .Where(m => m.ParentId == loserId)
            .ToListAsync(ct);

        // Computed early (rather than in the "Consolidate external IDs" section below) so the
        // merge log can record, per loser external ID, whether the winner already owned an
        // identical (Source, ExternalId) row. That distinction is essential for Unmerge: a
        // "duplicate" ID's row on the winner predates this merge and is the WINNER's own
        // identity, not the loser's — Unmerge must recreate a fresh copy for the restored stub
        // rather than stealing the winner's row (which was the original, unrecoverable bug).
        //
        // Queried fresh rather than read off winner.ExternalIds — a caller that loaded `winner`
        // without .Include(ExternalIds) would otherwise see this as empty (no lazy-loading
        // proxies configured), falsely concluding the winner owns none of its own external IDs.
        var winnerIdSet = (await dbContext.MediaExternalIds
            .Where(e => e.MediaItemId == winnerId)
            .Select(e => new { e.Source, e.ExternalId })
            .ToListAsync(ct))
            .Select(e => $"{e.Source}:{e.ExternalId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mergeLog = new MediaItemMerge
        {
            WinnerId            = winnerId,
            LoserOriginalId     = loserId,
            LoserName           = loser.Name,
            LoserMediaTypeId    = loser.MediaTypeId,
            LoserHierarchyLevel = loser.HierarchyLevel,
            LoserParentId       = loser.ParentId,
            LoserYear           = loser.Year,
            LoserNumber         = loser.Number,
            LoserExternalIdsJson = JsonSerializer.Serialize(
                loserExternalIds.Select(e => new LoserExternalId(
                    e.Source, e.ExternalId, winnerIdSet.Contains($"{e.Source}:{e.ExternalId}")))),
            LoserChildIdsJson   = JsonSerializer.Serialize(loserChildren.Select(c => c.Id)),
            LoserMetadataJson   = loser.MetadataJson,
            MergedAt            = DateTime.UtcNow,
            MergedByUserId      = mergedByUserId,
        };
        dbContext.MediaItemMerges.Add(mergeLog);

        // Repoint merge-log rows where the loser was itself a previous merge's winner.
        // Otherwise deleting the loser below cascades (MediaItemMerges.WinnerId is a cascading
        // FK) and permanently destroys that earlier merge's audit trail, making it impossible to
        // ever unmerge — even though all of the earlier loser's data is still fully intact, now
        // living inside the current winner. Retargeting keeps every merge in the chain
        // independently reversible.
        var priorMergesWonByLoser = await dbContext.MediaItemMerges
            .Where(m => m.WinnerId == loserId)
            .ToListAsync(ct);
        foreach (var priorMerge in priorMergesWonByLoser)
            priorMerge.WinnerId = winnerId;

        // ── AKA ───────────────────────────────────────────────────────────────
        // Skip episode-pattern names (e.g. "Show S01E03 - Title") — they are child items,
        // not real alternate titles, and would pollute the AKA line on the parent.
        if (NamesRequireAka(winner.Name, loser.Name)
            && !System.Text.RegularExpressions.Regex.IsMatch(loser.Name, @"S\d{1,2}E\d{1,2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            dbContext.MediaItemAliases.Add(new MediaItemAlias
            {
                MediaItemId = winnerId,
                Alias       = loser.Name,
                Source      = "merge",
                CreatedAt   = DateTime.UtcNow,
            });
        }

        // ── Consolidate external IDs onto winner ──────────────────────────────
        foreach (var eid in loserExternalIds)
        {
            if (winnerIdSet.Contains($"{eid.Source}:{eid.ExternalId}"))
                dbContext.MediaExternalIds.Remove(eid);
            else
                eid.MediaItemId = winnerId;
        }

        // ── Re-parent children ────────────────────────────────────────────────
        foreach (var child in loserChildren)
        {
            child.ParentId = winnerId;
            child.NormalizedName = MediaItemNormalizer.NormalizeName(child.Name);
        }

        // ── UserLibrary ───────────────────────────────────────────────────────
        var loserLibEntries = await dbContext.UserLibraries.Where(l => l.MediaItemId == loserId).ToListAsync(ct);
        var loserUserIds    = loserLibEntries.Select(l => l.UserId).ToList();
        var winnerLibByUser = loserUserIds.Count > 0
            ? await dbContext.UserLibraries
                .Where(l => l.MediaItemId == winnerId && loserUserIds.Contains(l.UserId))
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
                    // Loser had a better status — promote it entirely.
                    winnerLib.Status      = lib.Status;
                    winnerLib.CompletedAt = lib.CompletedAt ?? winnerLib.CompletedAt;
                    winnerLib.UserRating  = lib.UserRating  ?? winnerLib.UserRating;
                    winnerLib.UpdatedAt   = DateTime.UtcNow;
                }
                else
                {
                    // Same or lower rank — keep winner's status but fill in any missing data.
                    winnerLib.CompletedAt ??= lib.CompletedAt;
                    winnerLib.UserRating  ??= lib.UserRating;
                    winnerLib.UpdatedAt = DateTime.UtcNow;
                }
                dbContext.UserLibraries.Remove(lib);
            }
            else lib.MediaItemId = winnerId;
        }

        // ── InteractionEvents — re-point, but deduplicate by (user, timestamp) first ──────
        // A winner and loser can both carry an event for the same (UserId, Timestamp); blindly
        // re-pointing every loser row would violate that unique constraint. Discard the loser's
        // copy in that case — the data is identical.
        var loserEvents = await dbContext.InteractionEvents.Where(e => e.MediaItemId == loserId).ToListAsync(ct);
        if (loserEvents.Count > 0)
        {
            var winnerEventKeys = (await dbContext.InteractionEvents
                .Where(e => e.MediaItemId == winnerId)
                .Select(e => new { e.UserId, e.Timestamp })
                .ToListAsync(ct))
                .Select(e => new UserTimestampKey(e.UserId, e.Timestamp))
                .ToHashSet(new UserTimestampComparer());

            foreach (var ev in loserEvents)
            {
                if (winnerEventKeys.Contains(new UserTimestampKey(ev.UserId, ev.Timestamp)))
                    dbContext.InteractionEvents.Remove(ev);   // duplicate — discard
                else
                    ev.MediaItemId = winnerId;                // unique — re-point
            }
        }

        // ── MediaListItems ────────────────────────────────────────────────────
        var loserListItems = await dbContext.MediaListItems.Where(li => li.MediaItemId == loserId).ToListAsync(ct);
        foreach (var li in loserListItems) li.MediaItemId = winnerId;

        // ── MediaCredits — re-point; deduplicate by (person_name, role) ───────
        var loserCredits = await dbContext.MediaCredits.Where(c => c.MediaItemId == loserId).ToListAsync(ct);
        var winnerCreditSet = (await dbContext.MediaCredits
            .Where(c => c.MediaItemId == winnerId)
            .Select(c => new { c.PersonName, c.Role })
            .ToListAsync(ct))
            .Select(c => $"{c.PersonName}\0{c.Role}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var credit in loserCredits)
        {
            if (winnerCreditSet.Contains($"{credit.PersonName}\0{credit.Role}"))
                dbContext.MediaCredits.Remove(credit);
            else
                credit.MediaItemId = winnerId;
        }

        // ── metadata_json — merge blobs (winner blobs take precedence) ────────
        if (!string.IsNullOrEmpty(loser.MetadataJson))
        {
            try
            {
                var winnerBlobs = ParseMetadataBlobs(winner.MetadataJson);
                var loserBlobs  = ParseMetadataBlobs(loser.MetadataJson);
                foreach (var (key, val) in loserBlobs)
                    if (!winnerBlobs.ContainsKey(key) && key != "_resolved")
                        winnerBlobs[key] = val;
                winner.MetadataJson = JsonSerializer.Serialize(winnerBlobs);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to merge metadata_json blobs for winner {WinnerId} / loser {LoserId}; skipping blob merge",
                    winnerId, loserId);
            }
        }

        // ── Recompute _resolved ───────────────────────────────────────────────
        await resolutionService.ResolveAsync(winner, dbContext, ct);

        // ── Reset enrichment rows for plugins *newly introduced* by loser's IDs ─
        // Only reset for sources that were actually grafted onto the winner, not for
        // duplicate IDs that were deleted. Grafted = not already in winnerIdSet.
        var newSources = loserExternalIds
            .Where(e => !winnerIdSet.Contains($"{e.Source}:{e.ExternalId}"))
            .Select(e => e.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enrichmentRows = await dbContext.MediaEnrichments
            .Where(e => e.MediaItemId == winnerId)
            .ToListAsync(ct);
        foreach (var row in enrichmentRows)
        {
            var pluginShortId = PluginIdHelper.ToSource(row.PluginId);
            if (newSources.Contains(pluginShortId) &&
                row.Status is EnrichmentStatus.Completed or EnrichmentStatus.NotFound or EnrichmentStatus.Exhausted)
            {
                row.Status = EnrichmentStatus.Pending;
                row.RetryCount = 0;
                row.ErrorMessage = null;
            }
        }

        // ── NormalizedName on winner ──────────────────────────────────────────
        winner.NormalizedName = MediaItemNormalizer.NormalizeName(winner.Name);
        winner.UpdatedAt = DateTime.UtcNow;

        // ── Remove from duplicate candidates ─────────────────────────────────
        var candidates = await dbContext.MediaItemDuplicateCandidates
            .Where(c => (c.ItemAId == winnerId || c.ItemAId == loserId) &&
                        (c.ItemBId == winnerId || c.ItemBId == loserId))
            .ToListAsync(ct);
        dbContext.MediaItemDuplicateCandidates.RemoveRange(candidates);

        // ── Delete loser ──────────────────────────────────────────────────────
        dbContext.MediaItems.Remove(loser);
    }

    public async Task UnmergeAsync(int mergeId, CancellationToken ct = default)
    {
        var log = await db.MediaItemMerges
            .Include(m => m.Winner)
            .FirstOrDefaultAsync(m => m.Id == mergeId, ct)
            ?? throw new InvalidOperationException($"Merge record {mergeId} not found.");

        var winner = log.Winner!;

        // Wrap entire unmerge in a transaction so a failure after stub creation
        // doesn't leave a dangling empty stub with no external IDs or enrichment rows.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // ── Create stub for the loser ─────────────────────────────────────────
        var stub = new MediaItem
        {
            MediaTypeId    = log.LoserMediaTypeId,
            Name           = log.LoserName,
            HierarchyLevel = log.LoserHierarchyLevel,
            ParentId       = log.LoserParentId,
            Year           = log.LoserYear,
            Number         = log.LoserNumber,
            NormalizedName = MediaItemNormalizer.NormalizeName(log.LoserName),
            // Restore the loser's metadata blob so file paths (fileScanner.filePaths)
            // and any plugin data are available immediately after unmerge.
            MetadataJson   = log.LoserMetadataJson,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };
        db.MediaItems.Add(stub);
        await db.SaveChangesAsync(ct); // flush to get stub.Id; still inside the transaction

        // ── Split external IDs back ───────────────────────────────────────────
        List<LoserExternalId> loserIds;
        try   { loserIds = JsonSerializer.Deserialize<List<LoserExternalId>>(log.LoserExternalIdsJson) ?? []; }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Unmerge #{MergeId}: could not parse LoserExternalIdsJson; external IDs will not be restored", mergeId);
            loserIds = [];
        }
        if (loserIds.Count > 0)
        {
            // IDs the loser owned uniquely (grafted onto the winner during merge) vs. IDs the
            // loser shared with a winner that already had its own identical row (that row was
            // deleted, not moved, during merge). These need opposite restoration strategies:
            // a grafted row can safely be moved BACK from the winner to the stub, but a
            // duplicate row must never be taken from the winner — that row is the winner's own
            // pre-merge identity, not the loser's. It must be recreated fresh on the stub instead.
            var duplicateIds = loserIds.Where(l => l.WasDuplicate).ToList();
            var movedIds     = loserIds.Where(l => !l.WasDuplicate).ToList();

            foreach (var dup in duplicateIds)
            {
                db.MediaExternalIds.Add(new MediaExternalId
                {
                    MediaItemId = stub.Id,
                    Source      = dup.Source,
                    ExternalId  = dup.ExternalId,
                });
            }

            // Build lookup structures for the batched query and O(1) confirmation.
            var loserSources     = movedIds.Select(l => l.Source).ToList();
            var loserExtIds      = movedIds.Select(l => l.ExternalId).ToList();
            var loserIdSet       = movedIds
                .Select(l => $"{l.Source}:{l.ExternalId}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var winnerEids = movedIds.Count > 0
                ? await db.MediaExternalIds
                    .Where(e => e.MediaItemId == winner.Id
                             && loserSources.Contains(e.Source)
                             && loserExtIds.Contains(e.ExternalId))
                    .ToListAsync(ct)
                : [];
            // Track which sources have already been moved to the stub to avoid
            // creating duplicate ExternalId rows (can happen after repeated merge/unmerge cycles).
            var movedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var eid in winnerEids)
            {
                if (!loserIdSet.Contains($"{eid.Source}:{eid.ExternalId}")) continue;
                if (movedSources.Contains(eid.Source))
                {
                    // Duplicate source — this row is redundant; remove it rather than creating a second entry on the stub.
                    db.MediaExternalIds.Remove(eid);
                }
                else
                {
                    eid.MediaItemId = stub.Id;
                    movedSources.Add(eid.Source);
                }
            }
        }

        // ── Re-parent children ────────────────────────────────────────────────
        List<int> childIds;
        try   { childIds = JsonSerializer.Deserialize<List<int>>(log.LoserChildIdsJson) ?? []; }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Unmerge #{MergeId}: could not parse LoserChildIdsJson; children will not be re-parented", mergeId);
            childIds = [];
        }
        if (childIds.Count > 0)
        {
            var children = await db.MediaItems
                .Where(m => childIds.Contains(m.Id))
                .ToListAsync(ct);
            foreach (var child in children)
                child.ParentId = stub.Id;
        }

        // ── Clean winner metadata_json of loser plugin blobs ─────────────────
        if (!string.IsNullOrEmpty(winner.MetadataJson))
        {
            try
            {
                var blobs = ParseMetadataBlobs(winner.MetadataJson);
                var loserSources = loserIds.Select(l => l.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var src in loserSources)
                {
                    var keysToRemove = blobs.Keys
                        .Where(k => k != "_resolved" &&
                                    (k.EndsWith("." + src, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(k, src, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    foreach (var k in keysToRemove) blobs.Remove(k);
                }
                winner.MetadataJson = JsonSerializer.Serialize(blobs);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to clean metadata_json blobs during unmerge #{MergeId}; skipping blob cleanup",
                    mergeId);
            }
        }
        await resolutionService.ResolveAsync(winner, db, ct);

        // ── Reset winner enrichment rows for plugins whose IDs were returned ──
        // The winner's enrichment rows for these plugins still show Completed,
        // but the underlying external IDs are now on the stub. Reset them so the
        // winner is re-enriched from scratch and the UI doesn't show stale data.
        if (loserIds.Count > 0)
        {
            var returnedSources = loserIds.Select(l => l.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var winnerRows = await db.MediaEnrichments
                .Where(e => e.MediaItemId == winner.Id)
                .ToListAsync(ct);
            foreach (var row in winnerRows)
            {
                var shortId = PluginIdHelper.ToSource(row.PluginId);
                if (returnedSources.Contains(shortId))
                {
                    row.Status     = EnrichmentStatus.Pending;
                    row.RetryCount = 0;
                    row.ErrorMessage = null;
                    row.ExternalId = null;
                }
            }
        }

        // ── Remove AKA created by this merge ─────────────────────────────────
        var aka = await db.MediaItemAliases
            .FirstOrDefaultAsync(a => a.MediaItemId == winner.Id &&
                                       a.Alias == log.LoserName &&
                                       a.Source == "merge", ct);
        if (aka is not null) db.MediaItemAliases.Remove(aka);

        // ── Seed enrichment on stub ───────────────────────────────────────────
        var winnerEnrichments = await db.MediaEnrichments
            .Where(e => e.MediaItemId == winner.Id)
            .ToListAsync(ct);
        foreach (var row in winnerEnrichments)
        {
            db.MediaEnrichments.Add(new MediaItemEnrichment
            {
                MediaItemId = stub.Id,
                PluginId    = row.PluginId,
                Status      = EnrichmentStatus.Pending,
                MaxRetries  = 3,
            });
        }

        // ── Delete merge log ──────────────────────────────────────────────────
        db.MediaItemMerges.Remove(log);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "Unmerged merge #{MergeId}: recreated stub {StubId} ({Name}) from winner {WinnerId}",
            mergeId, stub.Id, stub.Name, winner.Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the two names are different enough to warrant an AKA entry.
    /// Identical normalized names → no AKA needed.
    /// </summary>
    internal static bool NamesRequireAka(string winnerName, string loserName)
    {
        var wn = MediaItemNormalizer.NormalizeName(winnerName);
        var ln = MediaItemNormalizer.NormalizeName(loserName);
        return !string.Equals(wn, ln, StringComparison.Ordinal);
    }

    private static int StatusRank(LibraryStatus status) => status switch
    {
        LibraryStatus.Rewatching  => 7,
        LibraryStatus.Completed   => 6,
        LibraryStatus.Watching    => 5,
        LibraryStatus.OnHold      => 4,
        LibraryStatus.PlanToWatch => 3,
        LibraryStatus.Dropped     => 2,
        _                         => 1, // Unwatched
    };

    // WasDuplicate defaults to false so logs written before this field existed still deserialize
    // (System.Text.Json falls back to the constructor parameter's default for a missing property).
    // false is the conservative choice there: it reproduces the pre-fix restore behavior for old
    // logs rather than guessing, since we have no way to know retroactively which of those rows
    // collided with a pre-existing winner ID.
    private record LoserExternalId(string Source, string ExternalId, bool WasDuplicate = false);

    // ── InteractionEvent deduplication helpers ────────────────────────────────

    private record UserTimestampKey(int UserId, DateTime Timestamp);

    private sealed class UserTimestampComparer : IEqualityComparer<UserTimestampKey>
    {
        public bool Equals(UserTimestampKey? x, UserTimestampKey? y) =>
            x is not null && y is not null
            && x.UserId == y.UserId
            && x.Timestamp == y.Timestamp;

        public int GetHashCode(UserTimestampKey obj) =>
            HashCode.Combine(obj.UserId, obj.Timestamp);
    }

    private static Dictionary<string, JsonElement> ParseMetadataBlobs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
