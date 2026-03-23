using Chronicle.Core.Models;
using Chronicle.Core.Models.Scan;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Scheduled task that scans all enabled scan folders nightly and
/// auto-imports groups whose confidence score meets the configured threshold.
/// Folders are scanned in parallel, bounded by the <c>scan.max_concurrency</c>
/// app setting (default: max(1, CPU cores / 4), hard cap: CPU core count).
/// </summary>
public sealed class ScheduledScanService : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _log = Log.ForContext<ScheduledScanService>();

    public ScheduledScanService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // ── IScheduledTask ────────────────────────────────────────────────────────

    public string TaskId      => "scheduled_scan";
    public string DisplayName => "Scheduled File Scan";
    public string Description => "Scans all enabled scan folders and auto-imports groups above the confidence threshold.";
    public string DefaultCron => "0 3 * * *";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        _log.Information("ScheduledScanService: Scheduled scan starting");

        // ── Load folders and concurrency setting in a short-lived scope ────────
        IReadOnlyList<ScanFolder> folders;
        int maxConcurrency;

        using (var setupScope = _scopeFactory.CreateScope())
        {
            var scanFolderSvc = setupScope.ServiceProvider.GetRequiredService<IScanFolderService>();
            var db            = setupScope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            var allFolders = await scanFolderSvc.GetAllAsync(ct);
            folders = allFolders.Where(f => f.IsEnabled).ToList();

            // scan.max_concurrency: default = max(1, ProcessorCount / 4), capped at ProcessorCount
            var concurrencySetting = await db.AppSettings.FindAsync(["scan.max_concurrency"], ct);
            int defaultConcurrency = Math.Max(1, Environment.ProcessorCount / 4);
            maxConcurrency = (concurrencySetting is not null
                              && int.TryParse(concurrencySetting.Value, out var mc)
                              && mc >= 1)
                ? Math.Min(mc, Environment.ProcessorCount)
                : defaultConcurrency;
        }

        if (folders.Count == 0)
        {
            _log.Information("ScheduledScanService: No enabled scan folders configured");
            return;
        }

        _log.Information(
            "ScheduledScanService: Scanning {Count} folder(s) — concurrency={Concurrency} (CPU cores={Cores})",
            folders.Count, maxConcurrency, Environment.ProcessorCount);

        // No user context in scheduled scans — UserLibrary rows are auto-created
        // for each user by LibraryService.GetForUserAsync on their first library view.
        IReadOnlyList<int> noUserIds = [];

        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        // Launch all folder scans; the semaphore bounds how many run at once.
        var folderTasks = folders
            .Select(folder => ScanOneFolderAsync(folder, noUserIds, semaphore, ct))
            .ToList();

        await Task.WhenAll(folderTasks);

        _log.Information("ScheduledScanService: Scheduled scan complete");
    }

    // ── Per-folder scan ───────────────────────────────────────────────────────

    private async Task ScanOneFolderAsync(
        ScanFolder folder,
        IReadOnlyList<int> noUserIds,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            if (ct.IsCancellationRequested) return;

            // Each folder gets its own DI scope — DbContext is not thread-safe.
            using var scope      = _scopeFactory.CreateScope();
            var fileScanSvc      = scope.ServiceProvider.GetRequiredService<IFileScanService>();
            var db               = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            var mediaTypeName    = folder.MediaType?.Name ?? string.Empty;
            var threshold        = await fileScanSvc.GetConfidenceThresholdAsync(mediaTypeName, ct);

            _log.Information(
                "ScheduledScanService: Scanning {Path} ({MediaType}, threshold={Threshold})",
                folder.Path,
                folder.MediaType?.DisplayName ?? "unknown type",
                threshold);

            var request = new ScanPreviewRequest(
                folder.Path,
                folder.Recursive,
                folder.MediaTypeId);

            ScanGroupResult scanResult = await fileScanSvc.PreviewGroupedAsync(request, ct);

            // ConfidenceScore on ScanGroup is 0.0–1.0; threshold is 0–100.
            double thresholdFraction = threshold / 100.0;
            var passingGroups = scanResult.Groups
                .Where(g => g.ConfidenceScore >= thresholdFraction)
                .ToList();

            var belowThreshold = scanResult.Groups
                .Where(g => g.ConfidenceScore < thresholdFraction)
                .ToList();

            if (belowThreshold.Count > 0)
            {
                _log.Warning(
                    "ScheduledScanService: {Count} group(s) below threshold ({Threshold}%) in {Path} — " +
                    "these will NOT be auto-imported. Use the File Scan page to review and accept them manually.",
                    belowThreshold.Count, threshold, folder.Path);

                foreach (var g in belowThreshold.Take(20))
                {
                    _log.Debug(
                        "  Skipped (confidence={Score:P0}): {Name}",
                        g.ConfidenceScore, g.Name);
                }

                if (belowThreshold.Count > 20)
                    _log.Debug("  … and {More} more skipped groups", belowThreshold.Count - 20);
            }

            if (passingGroups.Count == 0)
            {
                _log.Information(
                    "ScheduledScanService: No groups above threshold for {Path}",
                    folder.Path);
                var dbFolder0 = await db.ScanFolders.FindAsync([folder.Id], ct);
                if (dbFolder0 is not null)
                {
                    dbFolder0.LastScannedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                return;
            }

            _log.Information(
                "ScheduledScanService: Auto-importing {Count} group(s) from {Path} ({Below} below threshold, skipped)",
                passingGroups.Count, folder.Path, belowThreshold.Count);

            var importRequest = new ImportGroupsRequest(
                passingGroups.Select(g => ToImport(g)).ToList(),
                folder.MediaTypeId);

            var summary = await fileScanSvc.ImportGroupsAsync(importRequest, noUserIds, ct);

            _log.Information(
                "ScheduledScanService: Import complete for {Path} — " +
                "imported: {Imported} new, {Duplicates} already in library, {Failed} failed, {Below} skipped (below threshold)",
                folder.Path, summary.Imported, summary.Duplicates, summary.Failed, belowThreshold.Count);

            var dbFolder = await db.ScanFolders.FindAsync([folder.Id], ct);
            if (dbFolder is not null)
            {
                dbFolder.LastScannedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            // Fire-and-forget enrichment for newly imported items (non-blocking).
            // Use CancellationToken.None so enrichment isn't cancelled if the scan's token is cancelled.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var enrichScope = _scopeFactory.CreateScope();
                    var enrichSvc = enrichScope.ServiceProvider.GetRequiredService<IMetadataEnrichmentService>();
                    await enrichSvc.EnrichAllAsync(CancellationToken.None);
                }
                catch (Exception enrichEx)
                {
                    _log.Error(enrichEx, "ScheduledScanService: Background enrichment failed after scan of {Path}", folder.Path);
                }
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error scanning folder {Path}", folder.Path);
        }
        finally
        {
            semaphore.Release();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ScanGroupImport ToImport(ScanGroup group) => new(
        group.Name,
        group.Year,
        group.PosterPath,
        group.Children.Select(c => ToImport(c)).ToList(),
        group.Files,
        group.FolderPath,
        group.Number);
}
