using System.Text.Json;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// One pass of the Library Integrity Repair task: re-applies a person merge the user made that a later
/// identity split took apart.
///
/// The Person Identity Split pass moved a merged person's Wikipedia article onto a brand-new record whenever
/// the article's birth year disagreed with the other providers', without knowing the user had merged those two
/// records on purpose (David Fletcher, Christopher Denham and Eric Leach, merged 2026-10-10, were re-created the
/// next night and came back as duplicates to merge again). That pass now leaves a user's merge alone; this one
/// repairs the ones it had already undone: the article's record is merged back into the person the user chose.
///
/// Only an unmistakable case is touched: a merge by a USER (never an automatic resolver) into a person who still
/// exists and no longer holds the article, whose article now sits on a record created AFTER that merge, which
/// holds nothing but Wikipedia ids (what a split leaves behind). The merge is recorded as the same user's.
/// </summary>
public sealed class UserMergeRestoreService(
    IServiceScopeFactory scopeFactory,
    ILogger<UserMergeRestoreService> logger)
{
    private const string WikipediaSource = "wikipedia";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var merge = scope.ServiceProvider.GetRequiredService<IMergeService>();

        var userMerges = await db.MediaItemMerges
            .Where(m => m.MergedByUserId != null && m.LoserExternalIdsJson.Contains(WikipediaSource))
            .Select(m => new { m.WinnerId, m.MergedAt, m.MergedByUserId, m.LoserExternalIdsJson })
            .ToListAsync(ct);

        int restored = 0, failed = 0;
        foreach (var entry in userMerges)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var articleId in WikipediaIds(entry.LoserExternalIdsJson))
            {
                try
                {
                    var split = await FindSplitRecordAsync(db, entry.WinnerId, articleId, entry.MergedAt, ct);
                    if (split is null) continue;

                    await merge.MergeAsync(entry.WinnerId, split.Value, entry.MergedByUserId, ct);
                    restored++;
                    logger.LogInformation(
                        "User merge restored: person {SplitId} (article {Article}) merged back into {WinnerId}, as the user chose on {MergedAt}",
                        split.Value, articleId, entry.WinnerId, entry.MergedAt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    db.ChangeTracker.Clear();
                    logger.LogWarning(ex, "User merge restore: could not merge the record holding {Article} back into {WinnerId}", articleId, entry.WinnerId);
                }
            }
        }

        logger.LogInformation("User merge restore: re-merged {Restored} record(s) a split had taken apart ({Failed} failed)", restored, failed);
    }

    /// <summary>The record to merge back, or null when this merge was never undone (or the case is not unmistakable).</summary>
    private static async Task<int?> FindSplitRecordAsync(
        ChronicleDbContext db, int winnerId, string articleId, DateTime mergedAt, CancellationToken ct)
    {
        var winnerType = await db.MediaItems.Where(m => m.Id == winnerId)
            .Select(m => new { m.MediaTypeId, IsPerson = m.MediaType!.Name == "people" })
            .FirstOrDefaultAsync(ct);
        if (winnerType is null || !winnerType.IsPerson) return null;

        var holders = await db.MediaExternalIds
            .Where(e => e.Source == WikipediaSource && e.ExternalId == articleId)
            .Select(e => e.MediaItemId).ToListAsync(ct);
        if (holders.Count != 1 || holders[0] == winnerId) return null;   // still on the winner, or not on exactly one record

        var candidate = await db.MediaItems.Where(m => m.Id == holders[0])
            .Select(m => new { m.Id, m.CreatedAt, m.MediaTypeId, m.HierarchyLevel }).FirstOrDefaultAsync(ct);
        if (candidate is null || candidate.MediaTypeId != winnerType.MediaTypeId || candidate.HierarchyLevel != 0 ||
            candidate.CreatedAt <= mergedAt)
            return null;

        // A split leaves only the article behind. Anything else (a TMDB id, credits, children) means it has become
        // a record in its own right, which is not ours to fold away.
        var hasOtherIds = await db.MediaExternalIds.AnyAsync(e => e.MediaItemId == candidate.Id && e.Source != WikipediaSource, ct);
        var hasCredits = await db.MediaCredits.AnyAsync(c => c.PersonMediaItemId == candidate.Id, ct);
        var hasChildren = await db.MediaItems.AnyAsync(m => m.ParentId == candidate.Id, ct);
        return hasOtherIds || hasCredits || hasChildren ? null : candidate.Id;
    }

    private static IEnumerable<string> WikipediaIds(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object &&
                    e.TryGetProperty("Source", out var src) && src.ValueKind == JsonValueKind.String &&
                    string.Equals(src.GetString(), WikipediaSource, StringComparison.OrdinalIgnoreCase) &&
                    e.TryGetProperty("ExternalId", out var id) && id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(id.GetString()))
                    yield return id.GetString()!;
        }
    }
}
