using System.Text.Json;
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
        foreach (var lib in loserLibEntries)
        {
            var winnerLib = await db.UserLibraries
                .FirstOrDefaultAsync(l => l.MediaItemId == winnerId && l.UserId == lib.UserId, ct);
            if (winnerLib is not null)
            {
                if (StatusRank(lib.Status) > StatusRank(winnerLib.Status))
                {
                    winnerLib.Status      = lib.Status;
                    winnerLib.CompletedAt = lib.CompletedAt ?? winnerLib.CompletedAt;
                    winnerLib.UserRating  = lib.UserRating  ?? winnerLib.UserRating;
                    winnerLib.UpdatedAt   = DateTime.UtcNow;
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
        var winnerCreditKeys = await db.MediaCredits
            .Where(c => c.MediaItemId == winnerId)
            .Select(c => new { c.PersonName, c.Role })
            .ToListAsync(ct);
        foreach (var credit in loserCredits)
        {
            if (winnerCreditKeys.Any(k => k.PersonName == credit.PersonName && k.Role == credit.Role))
                db.MediaCredits.Remove(credit);
            else
                credit.MediaItemId = winnerId;
        }

        // ── metadata_json — merge blobs (winner blobs take precedence) ────────
        if (!string.IsNullOrEmpty(loser.MetadataJson))
        {
            var winnerBlobs = string.IsNullOrEmpty(winner.MetadataJson)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(winner.MetadataJson) ?? [];
            var loserBlobs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(loser.MetadataJson) ?? [];
            foreach (var (key, val) in loserBlobs)
                if (!winnerBlobs.ContainsKey(key) && key != "_resolved")
                    winnerBlobs[key] = val;
            winner.MetadataJson = JsonSerializer.Serialize(winnerBlobs);
        }

        // ── Recompute _resolved ───────────────────────────────────────────────
        await resolutionService.ResolveAsync(winner, db, ct);

        // ── Reset enrichment rows for plugins introduced by loser's external IDs
        var newSources = loserExternalIds.Select(e => e.Source).Distinct().ToList();
        var enrichmentRows = await db.MediaEnrichments
            .Where(e => e.MediaItemId == winnerId)
            .ToListAsync(ct);
        foreach (var row in enrichmentRows)
        {
            var pluginShortId = row.PluginId.Contains('.')
                ? row.PluginId.Split('.').Last()
                : row.PluginId;
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
        await db.SaveChangesAsync(ct); // need stub.Id

        // ── Split external IDs back ───────────────────────────────────────────
        var loserIds = JsonSerializer.Deserialize<List<LoserExternalId>>(log.LoserExternalIdsJson) ?? [];
        foreach (var lid in loserIds)
        {
            var eid = await db.MediaExternalIds
                .FirstOrDefaultAsync(e => e.MediaItemId == winner.Id &&
                                          e.Source == lid.Source &&
                                          e.ExternalId == lid.ExternalId, ct);
            if (eid is not null)
                eid.MediaItemId = stub.Id;
        }

        // ── Re-parent children ────────────────────────────────────────────────
        var childIds = JsonSerializer.Deserialize<List<int>>(log.LoserChildIdsJson) ?? [];
        foreach (var childId in childIds)
        {
            var child = await db.MediaItems.FindAsync([childId], ct);
            if (child is not null)
                child.ParentId = stub.Id;
        }

        // ── Clean winner metadata_json of loser plugin blobs ─────────────────
        if (!string.IsNullOrEmpty(winner.MetadataJson))
        {
            var blobs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(winner.MetadataJson) ?? [];
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
        await resolutionService.ResolveAsync(winner, db, ct);

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
}
