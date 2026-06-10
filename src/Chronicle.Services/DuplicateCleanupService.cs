using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Core.Helpers;
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
                    await using var tx = await context.Database.BeginTransactionAsync(ct);
                    await MergeAndDeleteAsync(context, resolutionService, winner, loser, ct);
                    await context.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    alreadyRemoved.Add(loser.Id);
                    removed++;
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
                    await MergeAndDeleteAsync(context, resolutionService, winner, loser, ct);
                    await context.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    alreadyRemoved.Add(loser.Id);
                    removed++;
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
                    await MergeAndDeleteAsync(context, resolutionService, winner, loser, ct);
                    await context.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    alreadyRemoved.Add(loser.Id);
                    removed++;
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
    /// </summary>
    private static async Task MergeAndDeleteAsync(
        ChronicleDbContext context,
        IMetadataResolutionService resolutionService,
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
        // Load full child entities here — re-used for both the snapshot IDs and re-parenting,
        // avoiding a second query later.
        var loserChildrenSnapshot = await context.MediaItems
            .Where(m => m.ParentId == loser.Id)
            .ToListAsync(ct);
        var loserChildIdsSnapshot = loserChildrenSnapshot.Select(m => m.Id).ToList();

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
            LoserMetadataJson    = loser.MetadataJson,
            MergedAt             = DateTime.UtcNow,
            MergedByUserId       = null, // automatic
        });

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
        catch (JsonException) { /* malformed JSON — treat as no path */ }
        return null;
    }

    private static Dictionary<string, System.Text.Json.JsonElement> ParseBlobs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

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
