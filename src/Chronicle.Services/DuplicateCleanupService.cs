using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
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
        using var scope = _scopeFactory.CreateScope();
        var context     = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

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
            .GroupBy(m => ExtractFilePath(m.MetadataJson) ?? string.Empty,
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
                // Oldest record wins — preserves the item the user has been tracking longest.
                var ordered = group.OrderBy(m => m.Id).ThenByDescending(ScoreItem).ToList();
                var winner = ordered[0];
                foreach (var loser in ordered.Skip(1))
                {
                    _log.Information(
                        "DuplicateCleanup: path '{Path}' — keeping {WId} ('{WName}'), removing {LId} ('{LName}')",
                        group.Key, winner.Id, winner.Name, loser.Id, loser.Name);
                    await MergeAndDeleteAsync(context, winner, loser, ct);
                    alreadyRemoved.Add(loser.Id);
                    removed++;
                }
            }
            await context.SaveChangesAsync(ct);
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
            .ToList();

        if (extIdGroups.Count > 0)
        {
            _log.Information("DuplicateCleanup: found {Count} external ID(s) shared by multiple items",
                extIdGroups.Count);

            foreach (var group in extIdGroups)
            {
                ct.ThrowIfCancellationRequested();

                var items = group
                    .Select(e => e.MediaItem!)
                    .DistinctBy(m => m.Id)
                    .Where(m => !alreadyRemoved.Contains(m.Id))
                    .ToList();

                if (items.Count < 2) continue;

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
                    await MergeAndDeleteAsync(context, winner, loser, ct);
                    alreadyRemoved.Add(loser.Id);
                    removed++;
                }
            }
            await context.SaveChangesAsync(ct);
        }

        // ── Pass 3: title-normalisation duplicates ─────────────────────────────
        // Catches file-scanner items like "Batman v Superman - Dawn of Justice (2016)"
        // that duplicated SIMKL stubs like "Batman v Superman: Dawn of Justice (2016)"
        // because Windows filenames cannot contain colons.
        // Groups root-level items by (normalised title, year, media type).
        var rootItems = await context.MediaItems
            .Include(m => m.ExternalIds)
            .Where(m => m.HierarchyLevel == 0)
            .ToListAsync(ct);

        var titleGroups = rootItems
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
                var items = group.Where(m => !alreadyRemoved.Contains(m.Id)).ToList();
                if (items.Count < 2) continue;

                // File-scanner items preferred (they have physical files).
                var ordered = items
                    .OrderByDescending(m => m.MetadataJson != null && m.MetadataJson.Contains("\"fileScanner\""))
                    .ThenByDescending(ScoreItem)
                    .ThenBy(m => m.Id)
                    .ToList();

                var winner = ordered[0];
                foreach (var loser in ordered.Skip(1))
                {
                    _log.Information(
                        "DuplicateCleanup: title-match '{Key}' — keeping {WId} ('{WName}'), removing {LId} ('{LName}')",
                        $"{group.Key.Item1} / {group.Key.Year}", winner.Id, winner.Name, loser.Id, loser.Name);
                    await MergeAndDeleteAsync(context, winner, loser, ct);
                    alreadyRemoved.Add(loser.Id);
                    removed++;
                }
            }
            await context.SaveChangesAsync(ct);
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
    /// </summary>
    private static async Task MergeAndDeleteAsync(
        ChronicleDbContext context,
        MediaItem winner,
        MediaItem loser,
        CancellationToken ct)
    {
        // ── Snapshot loser state BEFORE any re-pointing (for merge log) ──────────
        // These queries MUST run first — once ExternalIds and Children are re-parented
        // to the winner, querying by loser.Id returns nothing.
        var loserExtIdsSnapshot = await context.MediaExternalIds
            .Where(e => e.MediaItemId == loser.Id)
            .Select(e => new { e.Source, e.ExternalId })
            .ToListAsync(ct);
        var loserChildIdsSnapshot = await context.MediaItems
            .Where(m => m.ParentId == loser.Id)
            .Select(m => m.Id)
            .ToListAsync(ct);

        // ── UserLibrary ───────────────────────────────────────────────────────────
        // For each user who has the loser in their library: if the winner is already
        // there, merge — keeping the better status (e.g. Completed > Unwatched).
        var loserLibEntries = await context.UserLibraries
            .Where(l => l.MediaItemId == loser.Id)
            .ToListAsync(ct);
        var loserLibUserIds  = loserLibEntries.Select(l => l.UserId).ToList();
        var winnerLibByUser  = await context.UserLibraries
            .Where(l => l.MediaItemId == winner.Id && loserLibUserIds.Contains(l.UserId))
            .ToDictionaryAsync(l => l.UserId, ct);

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
        var loserEvents = await context.InteractionEvents
            .Where(e => e.MediaItemId == loser.Id)
            .ToListAsync(ct);

        foreach (var ev in loserEvents)
            ev.MediaItemId = winner.Id;

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
        var loserChildren = await context.MediaItems
            .Where(m => m.ParentId == loser.Id)
            .ToListAsync(ct);

        foreach (var child in loserChildren)
            child.ParentId = winner.Id;

        // ── MediaExternalIds — merge into winner, don't just delete ──────────────
        // Grafting the loser's IDs (e.g. "simkl:12345") onto the winner means
        // future syncs resolve at Stage 1 without re-creating the stub.
        var winnerIdSet = winner.ExternalIds
            .Select(e => $"{e.Source}:{e.ExternalId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

        // ── MediaEnrichments — remove loser's rows (winner keeps its own) ─────────
        context.MediaEnrichments.RemoveRange(
            await context.MediaEnrichments.Where(e => e.MediaItemId == loser.Id).ToListAsync(ct));

        // ── AKA ───────────────────────────────────────────────────────────────────
        if (MergeService.NamesRequireAka(winner.Name, loser.Name))
        {
            context.MediaItemAliases.Add(new Chronicle.Core.Models.MediaItemAlias
            {
                MediaItemId = winner.Id,
                Alias       = loser.Name,
                Source      = "merge",
                CreatedAt   = DateTime.UtcNow,
            });
        }

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
            LoserExternalIdsJson = System.Text.Json.JsonSerializer.Serialize(loserExtIdsSnapshot),
            LoserChildIdsJson    = System.Text.Json.JsonSerializer.Serialize(loserChildIdsSnapshot),
            MergedAt             = DateTime.UtcNow,
            MergedByUserId       = null, // automatic
        });

        // ── Stamp winner as modified ──────────────────────────────────────────────
        winner.UpdatedAt = DateTime.UtcNow;

        // ── Finally delete the loser ──────────────────────────────────────────────
        context.MediaItems.Remove(loser);
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
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            name, @"\s*[\(\[]\d{4}[\)\]]\s*$", "").Trim();
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

    private static string? ExtractFilePath(string? metadataJson)
    {
        if (metadataJson is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("fileScanner", out var scanner))
                return null;

            // Use the first individual file path as the duplicate key.
            // folderPath is explicitly NOT used — it is the parent directory of the item's
            // files (e.g. "/TV/Show/Season 1/") and is shared by every item in that folder.
            // Using it would incorrectly mark every episode in a season as a duplicate of
            // every other episode in the same folder.
            if (scanner.TryGetProperty("filePaths", out var fps)
                && fps.ValueKind == JsonValueKind.Array
                && fps.GetArrayLength() > 0)
                return fps[0].GetString();
        }
        catch { /* malformed JSON — treat as no path */ }
        return null;
    }
}
