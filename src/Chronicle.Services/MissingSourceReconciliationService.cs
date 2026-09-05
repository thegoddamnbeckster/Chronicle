using System.Text.Json;
using System.Text.Json.Nodes;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Nightly scheduled task that detects media items whose scanned file or folder no longer
/// exists on disk and clears the stale "fileScanner" LINK (folderPath/filePaths) -- while
/// keeping everything else about the item exactly as-is: enrichment, external ids, ratings,
/// library status, and even the rest of the fileScanner partition itself (importedAt, nfoPath,
/// nfoRaw, nfoParsed) as a historical record. Per-user request (2026-09-05): "the information
/// needs to be kept, but the link from the file scanner needs to go away."
///
/// This is deliberately the mirror image of a normal scan: FileScanService only ever looks at
/// folders that currently exist and creates/updates items for what it finds -- nothing ever
/// walks PREVIOUSLY known items to check whether their recorded location is still there, so
/// deleting a folder from disk left the catalog entry (and the "hasPhysicalFile"/"Missing"
/// badge, which reads this same fileScanner data live -- see FileIdentityJson.HasKnownFile)
/// unchanged forever. Clearing the link here is the only change needed: HasKnownFile then
/// naturally returns false on the very next page load, with no new frontend code.
///
/// Safety: before checking any individual item, the ScanFolder root its path falls under is
/// checked for reachability first (Directory.Exists) -- a root that's currently unreachable
/// (NAS asleep, SMB drop, a network blip) is skipped ENTIRELY for this run, rather than treating
/// every item under it as newly missing. This session's own Chronicle_Scraper work confirmed
/// exactly this failure mode is easy to get wrong: a single failed existence check on a
/// momentarily-dropped network share looks identical to the file genuinely being gone.
/// </summary>
public sealed class MissingSourceReconciliationService(
    IServiceScopeFactory scopeFactory,
    ILogger<MissingSourceReconciliationService> logger) : IScheduledTask
{
    public string TaskId      => "missing_source_reconciliation";
    public string DisplayName => "Missing Source Reconciliation";
    public string Description => "Detects media items whose scanned file or folder no longer exists on disk and clears the stale File Scanner link, keeping everything else about the item (ratings, enrichment, external IDs) untouched.";
    public string DefaultCron => "0 4 * * *"; // 4 AM nightly, after the 2-3 AM duplicate scans

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var roots = await db.ScanFolders
            .Where(f => f.IsEnabled)
            .Select(f => f.Path)
            .ToListAsync(ct);

        // Reachability gate: a root whose own top-level path can't be seen right now is
        // skipped entirely -- every item under it is left exactly as it is this run, not
        // treated as newly missing. Ordered longest-first so a nested root (e.g.
        // "E:\Music\Live Albums" under "E:\Music") matches its own, more specific entry.
        var reachableRoots = new List<string>();
        foreach (var root in roots.OrderByDescending(r => r.Length))
        {
            if (Directory.Exists(root))
                reachableRoots.Add(root);
            else
                logger.LogWarning(
                    "Missing-source reconciliation: root {Root} is not reachable right now -- " +
                    "skipping every item under it this run rather than marking them missing.", root);
        }
        if (reachableRoots.Count == 0)
        {
            logger.LogInformation("Missing-source reconciliation: no reachable scan roots this run -- nothing to check.");
            return;
        }

        // Only items whose MetadataJson actually mentions "fileScanner" are candidates -- a
        // simple LIKE filter server-side avoids deserializing every item in the catalog just
        // to find the handful with no fileScanner partition at all. Loaded as tracked entities
        // (not a projection) so a match can be mutated and saved directly, no second per-item
        // fetch needed.
        var candidates = await db.MediaItems
            .Where(m => m.MetadataJson != null && EF.Functions.Like(m.MetadataJson, "%fileScanner%"))
            .ToListAsync(ct);

        var cleared = 0;
        foreach (var item in candidates)
        {
            ct.ThrowIfCancellationRequested();

            JsonObject root;
            try
            {
                root = JsonNode.Parse(item.MetadataJson!)?.AsObject() ?? new JsonObject();
            }
            catch (JsonException)
            {
                continue; // malformed blob -- not this task's job to repair
            }
            if (root["fileScanner"] is not JsonObject fs) continue;

            var folderPath = fs["folderPath"]?.GetValue<string>();
            var filePaths  = fs["filePaths"]?.AsArray()
                ?.Select(n => n?.GetValue<string>())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToList();

            // Nothing recorded to check (a container item with neither field set yet) -- skip.
            bool hasFolderPath = !string.IsNullOrEmpty(folderPath);
            bool hasFilePaths  = filePaths is { Count: > 0 };
            if (!hasFolderPath && !hasFilePaths) continue;

            // Only act when the relevant path's OWN root is one of this run's reachable roots --
            // an item under a currently-unreachable root is left untouched (see the gate above).
            var pathToCheckRootOf = hasFolderPath ? folderPath! : filePaths![0];
            var owningRoot = reachableRoots.FirstOrDefault(r =>
                pathToCheckRootOf.StartsWith(r, StringComparison.OrdinalIgnoreCase));
            if (owningRoot is null) continue;

            var stillPresent = hasFolderPath
                ? Directory.Exists(folderPath)
                : filePaths!.Any(File.Exists);
            if (stillPresent) continue;

            // Gone -- clear only the link (folderPath/filePaths). Every other fileScanner
            // field (importedAt, nfoPath, nfoRaw, nfoParsed) and every other partition in
            // MetadataJson is left exactly as it was.
            fs.Remove("folderPath");
            fs.Remove("filePaths");
            var tracked = await db.MediaItems.FirstAsync(m => m.Id == item.Id, ct);
            tracked.MetadataJson = root.ToJsonString();
            cleared++;

            logger.LogInformation(
                "Missing-source reconciliation: item {ItemId} -- recorded source {Path} no longer exists, cleared the File Scanner link.",
                item.Id, pathToCheckRootOf);
        }

        if (cleared > 0)
            await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Missing-source reconciliation complete: checked {Checked} candidate(s), cleared {Cleared} stale link(s).",
            candidates.Count, cleared);
    }
}
