using Chronicle.Core.Models;
using Chronicle.Core.Models.Scan;
using Chronicle.Data;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Scheduled task that scans all enabled scan folders nightly and
/// auto-imports groups whose confidence score meets the configured threshold.
///
/// Execution is split into two phases:
///   Phase 1 — Preview (parallel, filesystem reads only): discover which groups pass
///              the confidence threshold and compute the grand total file count.
///   Phase 2 — Import (sequential, DB writes): persist each folder's groups in order,
///              accumulating progress against the grand total so the progress counter
///              never resets mid-run when multiple media-type folders are scanned.
/// </summary>
public sealed class ScheduledScanService : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ImportProgressService _importProgress;
    private readonly IPluginRegistry _registry;
    private readonly ILogger _log = Log.ForContext<ScheduledScanService>();

    public ScheduledScanService(IServiceScopeFactory scopeFactory, ImportProgressService importProgress, IPluginRegistry registry)
    {
        _scopeFactory    = scopeFactory;
        _importProgress  = importProgress;
        _registry        = registry;
    }

    // ── IScheduledTask ────────────────────────────────────────────────────────

    public string TaskId      => "scheduled_scan";
    public string DisplayName => "Scheduled File Scan";
    public string Description => "Scans all enabled scan folders and auto-imports groups above the confidence threshold.";
    public string DefaultCron => "0 3 * * *";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        _log.Information("ScheduledScanService: Scheduled scan starting");

        // ── Load folders and concurrency setting ─────────────────────────────
        IReadOnlyList<ScanFolder> folders;
        int maxConcurrency;

        using (var setupScope = _scopeFactory.CreateScope())
        {
            var scanFolderSvc = setupScope.ServiceProvider.GetRequiredService<IScanFolderService>();

            var allFolders = await scanFolderSvc.GetAllAsync(ct);
            folders = allFolders.Where(f => f.IsEnabled).ToList();

            // Read max_concurrency from the file scanner plugin settings.
            // 0 from the plugin means "auto": max(1, CPU cores / 4), capped at core count.
            int defaultConcurrency = Math.Max(1, Environment.ProcessorCount / 4);
            var configuredConcurrency = _registry.GetFileScannerPlugins().FirstOrDefault()?.MaxConcurrency ?? 0;
            maxConcurrency = configuredConcurrency >= 1
                ? Math.Min(configuredConcurrency, Environment.ProcessorCount)
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

        // Signal immediately so the UI shows activity during the (potentially long) preview phase.
        _importProgress.UpdateStatus("Scanning for new files…");

        // ── Phase 1: Preview all folders in parallel ──────────────────────────
        // Filesystem reads are safe to parallelise; DB writes come later.
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var previewTasks = folders
            .Select(folder => PreviewFolderAsync(folder, semaphore, ct))
            .ToList();
        var previews = await Task.WhenAll(previewTasks);

        // ── Phase 2: Compute grand total, start progress, import sequentially ─
        int grandTotal = previews.Sum(p => p.PassingFileCount);

        if (grandTotal == 0)
        {
            _log.Information("ScheduledScanService: No files to import — all folders at or below confidence threshold");
            return;
        }

        _importProgress.Start(grandTotal);

        int offset = 0;
        int totalImported = 0, totalFailed = 0, totalDuplicates = 0;
        var allFailures = new List<string>();

        foreach (var preview in previews)
        {
            if (ct.IsCancellationRequested) break;

            if (preview.PassingGroups.Count == 0)
            {
                await TouchLastScannedAtAsync(preview.Folder, ct);
                continue;
            }

            _log.Information(
                "ScheduledScanService: Auto-importing {Count} group(s) from {Path} ({Below} below threshold, skipped)",
                preview.PassingGroups.Count, preview.Folder.Path, preview.BelowThresholdCount);

            _importProgress.UpdateStatus($"Importing: {preview.Folder.Path}");

            var importRequest = new ImportGroupsRequest(preview.PassingGroups, preview.Folder.MediaTypeId);

            using var importScope = _scopeFactory.CreateScope();
            var fileScanSvc = importScope.ServiceProvider.GetRequiredService<IFileScanService>();
            var db          = importScope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            var summary = await fileScanSvc.ImportGroupsAsync(
                importRequest, noUserIds, ct,
                progressOffset: offset,
                manageProgress: false);

            totalImported   += summary.Imported;
            totalFailed     += summary.Failed;
            totalDuplicates += summary.Duplicates;
            allFailures.AddRange(summary.Failures);
            offset += preview.PassingFileCount;

            _log.Information(
                "ScheduledScanService: Import complete for {Path} — " +
                "imported: {Imported} new, {Duplicates} already in library, {Failed} failed, {Below} skipped (below threshold)",
                preview.Folder.Path, summary.Imported, summary.Duplicates, summary.Failed, preview.BelowThresholdCount);

            var dbFolder = await db.ScanFolders.FindAsync([preview.Folder.Id], ct);
            if (dbFolder is not null)
            {
                dbFolder.LastScannedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            // Fire-and-forget enrichment for newly imported items (non-blocking).
            // Use CancellationToken.None so enrichment isn't cancelled if the scan token is cancelled.
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
                    _log.Error(enrichEx, "ScheduledScanService: Background enrichment failed after scan of {Path}", preview.Folder.Path);
                }
            });
        }

        _importProgress.Complete(new ImportProgressResult
        {
            Imported   = totalImported,
            Failed     = totalFailed,
            Failures   = allFailures,
            Duplicates = totalDuplicates,
            TotalFiles = grandTotal,
        });

        _log.Information("ScheduledScanService: Scheduled scan complete");
    }

    // ── Phase 1 helper: preview one folder (filesystem reads, parallel-safe) ─

    private sealed record FolderPreview(
        ScanFolder Folder,
        List<ScanGroupImport> PassingGroups,
        int PassingFileCount,
        int BelowThresholdCount);

    private async Task<FolderPreview> PreviewFolderAsync(
        ScanFolder folder,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            if (ct.IsCancellationRequested)
                return new FolderPreview(folder, [], 0, 0);

            _importProgress.UpdateStatus($"Scanning: {folder.Path}");

            using var scope     = _scopeFactory.CreateScope();
            var fileScanSvc     = scope.ServiceProvider.GetRequiredService<IFileScanService>();

            var threshold = await fileScanSvc.GetConfidenceThresholdAsync(folder.MediaType?.Name ?? string.Empty, ct);

            _log.Information(
                "ScheduledScanService: Previewing {Path} ({MediaType}, threshold={Threshold})",
                folder.Path,
                folder.MediaType?.DisplayName ?? "unknown type",
                threshold);

            var request     = new ScanPreviewRequest(folder.Path, folder.Recursive, folder.MediaTypeId);
            var scanResult  = await fileScanSvc.PreviewGroupedAsync(request, ct);

            double thresholdFraction = threshold / 100.0;
            var passing = scanResult.Groups
                .Where(g => g.ConfidenceScore >= thresholdFraction)
                .ToList();
            var below = scanResult.Groups
                .Where(g => g.ConfidenceScore < thresholdFraction)
                .ToList();

            if (below.Count > 0)
            {
                _log.Warning(
                    "ScheduledScanService: {Count} group(s) below threshold ({Threshold}%) in {Path} — " +
                    "these will NOT be auto-imported. Use the File Scan page to review and accept them manually.",
                    below.Count, threshold, folder.Path);

                foreach (var g in below.Take(20))
                    _log.Debug("  Skipped (confidence={Score:P0}): {Name}", g.ConfidenceScore, g.Name);

                if (below.Count > 20)
                    _log.Debug("  … and {More} more skipped groups", below.Count - 20);
            }

            if (passing.Count == 0)
            {
                _log.Information("ScheduledScanService: No groups above threshold for {Path}", folder.Path);
                return new FolderPreview(folder, [], 0, below.Count);
            }

            var importGroups = passing.Select(FileScanService.ToScanGroupImport).ToList();
            int fileCount    = importGroups.Sum(g => g.TotalFileCount);

            return new FolderPreview(folder, importGroups, fileCount, below.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error previewing folder {Path}", folder.Path);
            return new FolderPreview(folder, [], 0, 0);
        }
        finally
        {
            semaphore.Release();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task TouchLastScannedAtAsync(ScanFolder folder, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var dbFolder = await db.ScanFolders.FindAsync([folder.Id], ct);
            if (dbFolder is not null)
            {
                dbFolder.LastScannedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ScheduledScanService: Could not update LastScannedAt for folder {Id}", folder.Id);
        }
    }

}
