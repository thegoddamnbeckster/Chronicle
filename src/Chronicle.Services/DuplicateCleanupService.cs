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
        using var scope   = _scopeFactory.CreateScope();
        var context       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Load every item that has file-scanner metadata — these are the only ones that
        // can have a physical file path to match on.
        var itemsWithPaths = await context.MediaItems
            .Include(m => m.ExternalIds)
            .Where(m => m.MetadataJson != null
                     && EF.Functions.Like(m.MetadataJson, "%fileScanner%"))
            .ToListAsync(ct);

        // Group by canonical (lower-case) individual file path; keep only groups with > 1 member.
        // NOTE: we intentionally use the first entry from filePaths[], NOT folderPath.
        // folderPath is the parent directory of the item's files and is shared by every
        // item in the same folder (e.g. every episode in a season).  Using it as the key
        // would incorrectly group all items in a folder as duplicates of each other.
        var duplicateGroups = itemsWithPaths
            .GroupBy(m => ExtractFilePath(m.MetadataJson) ?? string.Empty,
                     StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1)
            .ToList();

        if (duplicateGroups.Count == 0)
            return 0;

        _log.Information("DuplicateCleanup: found {GroupCount} file path(s) with duplicate items",
            duplicateGroups.Count);

        int removed = 0;

        foreach (var group in duplicateGroups)
        {
            ct.ThrowIfCancellationRequested();

            // Oldest record wins (lowest Id = earliest imported).  This preserves the
            // item the user has been tracking longest.  Metadata score breaks ties when
            // two items share the same Id (not normally possible, but defensive).
            var ordered = group
                .OrderBy(m => m.Id)
                .ThenByDescending(ScoreItem)
                .ToList();

            var winner = ordered[0];
            var losers = ordered.Skip(1).ToList();

            _log.Information(
                "DuplicateCleanup: path '{Path}' — keeping item {WinnerId} ('{WinnerName}'), removing {Count} duplicate(s)",
                group.Key, winner.Id, winner.Name, losers.Count);

            foreach (var loser in losers)
            {
                await MergeAndDeleteAsync(context, winner, loser, ct);
                removed++;
            }
        }

        await context.SaveChangesAsync(ct);

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

        // ── Owned records — simply delete (not worth migrating) ──────────────────
        context.MediaExternalIds.RemoveRange(
            await context.MediaExternalIds.Where(e => e.MediaItemId == loser.Id).ToListAsync(ct));

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
