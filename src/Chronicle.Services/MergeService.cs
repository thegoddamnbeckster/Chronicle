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

        if (winner.MediaTypeId != loser.MediaTypeId || winner.HierarchyLevel != loser.HierarchyLevel)
            throw new InvalidOperationException("Items must share the same media type and hierarchy level.");

        // Non-root items (seasons, episodes, tracks) must share the same parent — merging
        // a season from one show into a season from another would corrupt the hierarchy.
        if (winner.HierarchyLevel > 0 && winner.ParentId != loser.ParentId)
            throw new InvalidOperationException(
                "Non-root items must share the same parent item to be merged.");

        // Reject if either item is referenced as a loser_original_id (already deleted)
        var alreadyMerged = await db.MediaItemMerges
            .AnyAsync(m => m.LoserOriginalId == winnerId || m.LoserOriginalId == loserId, ct);
        if (alreadyMerged)
            throw new InvalidOperationException("One of these items has already been merged and deleted.");

        // ── Snapshot for merge log ─────────────────────────────────────────────
        var loserExternalIds = await db.MediaExternalIds
            .Where(e => e.MediaItemId == loserId)
            .ToListAsync(ct);

        var loserChildren = await db.MediaItems
            .Where(m => m.ParentId == loserId)
            .ToListAsync(ct);

        var mergeLog = new MediaItemMerge
        {
            WinnerId            = winnerId,
            LoserOriginalId     = loserId,
            LoserName           = loser.Name,
            LoserMediaTypeId    = loser.MediaTypeId,
            LoserHierarchyLevel = loser.HierarchyLevel,
            LoserParentId       = loser.ParentId,
            LoserExternalIdsJson = JsonSerializer.Serialize(
                loserExternalIds.Select(e => new { e.Source, e.ExternalId })),
            LoserChildIdsJson   = JsonSerializer.Serialize(loserChildren.Select(c => c.Id)),
            MergedAt            = DateTime.UtcNow,
            MergedByUserId      = mergedByUserId,
        };
        db.MediaItemMerges.Add(mergeLog);

        // ── AKA ───────────────────────────────────────────────────────────────
        if (NamesRequireAka(winner.Name, loser.Name))
        {
            db.MediaItemAliases.Add(new MediaItemAlias
            {
                MediaItemId = winnerId,
                Alias       = loser.Name,
                Source      = "merge",
                CreatedAt   = DateTime.UtcNow,
            });
        }

        // ── Consolidate external IDs onto winner ──────────────────────────────
        var winnerIdSet = (await db.MediaExternalIds
            .Where(e => e.MediaItemId == winnerId)
            .Select(e => new { e.Source, e.ExternalId })
            .ToListAsync(ct))
            .Select(e => $"{e.Source}:{e.ExternalId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var eid in loserExternalIds)
        {
            if (winnerIdSet.Contains($"{eid.Source}:{eid.ExternalId}"))
                db.MediaExternalIds.Remove(eid);
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
        var loserLibEntries = await db.UserLibraries.Where(l => l.MediaItemId == loserId).ToListAsync(ct);
        var loserUserIds    = loserLibEntries.Select(l => l.UserId).ToList();
        var winnerLibByUser = loserUserIds.Count > 0
            ? await db.UserLibraries
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
                db.UserLibraries.Remove(lib);
            }
            else lib.MediaItemId = winnerId;
        }

        // ── InteractionEvents ─────────────────────────────────────────────────
        var loserEvents = await db.InteractionEvents.Where(e => e.MediaItemId == loserId).ToListAsync(ct);
        foreach (var ev in loserEvents) ev.MediaItemId = winnerId;

        // ── MediaListItems ────────────────────────────────────────────────────
        var loserListItems = await db.MediaListItems.Where(li => li.MediaItemId == loserId).ToListAsync(ct);
        foreach (var li in loserListItems) li.MediaItemId = winnerId;

        // ── MediaCredits — re-point; deduplicate by (person_name, role) ───────
        var loserCredits = await db.MediaCredits.Where(c => c.MediaItemId == loserId).ToListAsync(ct);
        var winnerCreditSet = (await db.MediaCredits
            .Where(c => c.MediaItemId == winnerId)
            .Select(c => new { c.PersonName, c.Role })
            .ToListAsync(ct))
            .Select(c => $"{c.PersonName}\0{c.Role}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var credit in loserCredits)
        {
            if (winnerCreditSet.Contains($"{credit.PersonName}\0{credit.Role}"))
                db.MediaCredits.Remove(credit);
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
        await resolutionService.ResolveAsync(winner, db, ct);

        // ── Reset enrichment rows for plugins *newly introduced* by loser's IDs ─
        // Only reset for sources that were actually grafted onto the winner, not for
        // duplicate IDs that were deleted. Grafted = not already in winnerIdSet.
        var newSources = loserExternalIds
            .Where(e => !winnerIdSet.Contains($"{e.Source}:{e.ExternalId}"))
            .Select(e => e.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enrichmentRows = await db.MediaEnrichments
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
        var candidates = await db.MediaItemDuplicateCandidates
            .Where(c => (c.ItemAId == winnerId || c.ItemAId == loserId) &&
                        (c.ItemBId == winnerId || c.ItemBId == loserId))
            .ToListAsync(ct);
        db.MediaItemDuplicateCandidates.RemoveRange(candidates);

        // ── Delete loser ──────────────────────────────────────────────────────
        db.MediaItems.Remove(loser);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Merged item {LoserId} ({LoserName}) into {WinnerId} ({WinnerName})",
            loserId, loser.Name, winnerId, winner.Name);
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
            NormalizedName = MediaItemNormalizer.NormalizeName(log.LoserName),
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
            // Build lookup structures for the batched query and O(1) confirmation.
            var loserSources     = loserIds.Select(l => l.Source).ToList();
            var loserExtIds      = loserIds.Select(l => l.ExternalId).ToList();
            var loserIdSet       = loserIds
                .Select(l => $"{l.Source}:{l.ExternalId}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var winnerEids = await db.MediaExternalIds
                .Where(e => e.MediaItemId == winner.Id
                         && loserSources.Contains(e.Source)
                         && loserExtIds.Contains(e.ExternalId))
                .ToListAsync(ct);
            foreach (var eid in winnerEids)
            {
                if (loserIdSet.Contains($"{eid.Source}:{eid.ExternalId}"))
                    eid.MediaItemId = stub.Id;
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

    private record LoserExternalId(string Source, string ExternalId);

    private static Dictionary<string, JsonElement> ParseMetadataBlobs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
