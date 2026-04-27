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

        // Build a set of item IDs already removed in Pass 1 so we don't process them again.
        var alreadyRemoved = new HashSet<int>();

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
        // ── UserLibrary ───────────────────────────────────────────────────────────
        // For each user who has the loser in their library: if the winner is already
        // there, drop the loser entry; otherwise re-point it to the winner.
        var loserLibEntries = await context.UserLibraries
            .Where(l => l.MediaItemId == loser.Id)
            .ToListAsync(ct);

        foreach (var lib in loserLibEntries)
        {
            var winnerAlreadyPresent = await context.UserLibraries
                .AnyAsync(l => l.MediaItemId == winner.Id && l.UserId == lib.UserId, ct);

            if (winnerAlreadyPresent)
                context.UserLibraries.Remove(lib);
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

        // ── Finally delete the loser ──────────────────────────────────────────────
        context.MediaItems.Remove(loser);
    }

    /// <summary>
    /// Scores a media item for survivor selection.
    /// Higher is better.  Items with more metadata are preferred.
    /// </summary>
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
