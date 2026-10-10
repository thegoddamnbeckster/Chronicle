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

            // A folder with its own media type is one import; a folder that sorts files automatically is one import per
            // type its groups landed in. (A group never needs the folder's type when it carries its own.)
            var requests = ImportGrouping.ByType(preview.PassingGroups, preview.Folder.MediaTypeId ?? 0, preview.BundleRelatedFiles);

            using var importScope = _scopeFactory.CreateScope();
            var fileScanSvc = importScope.ServiceProvider.GetRequiredService<IFileScanService>();
            var db          = importScope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            int imported = 0, failed = 0, duplicates = 0;
            var failures = new List<string>();
            var runningOffset = offset;
            foreach (var importRequest in requests)
            {
                var part = await fileScanSvc.ImportGroupsAsync(
                    importRequest, noUserIds, ct,
                    progressOffset: runningOffset,
                    manageProgress: false);
                imported += part.Imported; failed += part.Failed; duplicates += part.Duplicates;
                failures.AddRange(part.Failures);
                runningOffset += importRequest.Groups.Sum(g => g.TotalFileCount);
            }
            var summary = new ImportApprovedSummary(imported, failed, failures, duplicates);

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

            // The nightly scan imports on its own; this is how anyone finds out it found something.
            if (summary.Imported > 0)
                await (importScope.ServiceProvider.GetService<Notifications.INotificationService>()?.NotifyAdminsAsync(
                    Chronicle.Core.Models.NotificationKinds.ScanImported,
                    $"{summary.Imported} new item{(summary.Imported == 1 ? "" : "s")} found in {preview.Folder.Path}",
                    summary.Failed > 0 ? $"{summary.Failed} could not be imported." : null,
                    "/library", ct: ct) ?? Task.FromResult(0));

            // New file(s) may be invisible to every Kodi device's own VideoLibrary until it
            // runs its own local scan -- VideoLibrary.Refresh* only works on an item Kodi
            // already has a library entry for, so a brand-new file needs a real
            // VideoLibrary.Scan instead. Signals the pull-based flag each device's own poll
            // checks (see IKodiDeviceService.SignalNewContentAsync's own doc); cheap (one
            // upsert), so no need for a background Task.Run the way enrichment below gets.
            if (summary.Imported > 0)
            {
                var kodiDevices = importScope.ServiceProvider.GetRequiredService<IKodiDeviceService>();
                foreach (var typeId in requests.Select(r => r.MediaTypeId).Distinct())
                {
                    var typeName = await db.MediaTypes.AsNoTracking().Where(t => t.Id == typeId).Select(t => t.Name).FirstOrDefaultAsync(ct);
                    if (typeName is not null)
                        await kodiDevices.SignalNewContentAsync(typeName, ct);
                }
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
        int BelowThresholdCount,
        bool BundleRelatedFiles = false);

    public const string MismatchActionKey = "scan.mismatch_action";
    public const string BundleRelatedFilesKey = "scan.bundle_related_files";

    /// <summary>Splits groups into those safe to import and those that look like a different media type. With the
    /// "flag" action (the default) the second kind is held back for a person to look at; with "ignore" nothing is held.</summary>
    internal static (List<ScanGroup> Import, List<ScanGroup> Held) HoldBackMismatches(IEnumerable<ScanGroup> groups, string? action)
    {
        var all = groups.ToList();
        if (string.Equals(action, "ignore", StringComparison.OrdinalIgnoreCase))
            return (all, []);
        return (all.Where(g => g.SuggestedMediaTypeId is null).ToList(), all.Where(g => g.SuggestedMediaTypeId is not null).ToList());
    }

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

            // A folder with its own type has one threshold; a folder that sorts files automatically has the threshold of
            // whichever type each group lands in.
            bool detect = folder.MediaTypeId is null;
            var threshold = await fileScanSvc.GetConfidenceThresholdAsync(folder.MediaType?.Name ?? string.Empty, ct);

            _log.Information(
                "ScheduledScanService: Previewing {Path} ({MediaType}, threshold={Threshold})",
                folder.Path,
                detect ? "each file sorted into its own type" : folder.MediaType?.DisplayName ?? "unknown type",
                detect ? "per type" : threshold);

            var request     = new ScanPreviewRequest(folder.Path, folder.Recursive, folder.MediaTypeId ?? ScanPreviewRequest.AutoDetect);
            var scanResult  = await fileScanSvc.PreviewGroupedAsync(request, ct);

            var thresholds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (detect)
                foreach (var key in scanResult.Groups.Select(g => g.MediaTypeKey).Where(k => k is not null).Distinct(StringComparer.OrdinalIgnoreCase))
                    thresholds[key!] = await fileScanSvc.GetConfidenceThresholdAsync(key!, ct);
            double ThresholdFor(ScanGroup g) =>
                (detect && g.MediaTypeKey is not null && thresholds.TryGetValue(g.MediaTypeKey, out var t) ? t : threshold) / 100.0;

            var passing = scanResult.Groups
                .Where(g => g.ConfidenceScore >= ThresholdFor(g))
                .ToList();

            // Settings read once per folder: what to do with files that look like another media type, and
            // whether to remember subtitles / artwork / extras with their items.
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var settings = await db.AppSettings.AsNoTracking()
                .Where(a => a.Key == MismatchActionKey || a.Key == BundleRelatedFilesKey)
                .ToDictionaryAsync(a => a.Key, a => a.Value, ct);
            settings.TryGetValue(MismatchActionKey, out var mismatchAction);
            bool bundle = folder.BundleRelatedFiles
                          ?? (settings.TryGetValue(BundleRelatedFilesKey, out var bundleText)
                              && bool.TryParse(bundleText, out var b) && b);

            var (importable, held) = HoldBackMismatches(passing, mismatchAction);
            passing = importable;
            if (held.Count > 0)
            {
                foreach (var g in held)
                    _log.Warning("ScheduledScanService: held back '{Name}' in {Path}: {Reason}", g.Name, folder.Path, g.SuggestedMediaTypeReason);
                var notifier = scope.ServiceProvider.GetService<Notifications.INotificationService>();
                if (notifier is not null)
                    await notifier.NotifyAdminsAsync(Chronicle.Core.Models.NotificationKinds.ScanReview,
                        $"{held.Count} item{(held.Count == 1 ? "" : "s")} in {folder.Path} may be the wrong type",
                        string.Join("; ", held.Take(3).Select(g => $"{g.Name}: looks like {g.SuggestedMediaTypeName}")),
                        "/scan", dedupeKey: $"scan.review:{folder.Id}", ct: ct);
            }
            var below = scanResult.Groups
                .Where(g => g.ConfidenceScore < ThresholdFor(g))
                .ToList();

            if (below.Count > 0)
            {
                _log.Warning(
                    "ScheduledScanService: {Count} group(s) below threshold ({Threshold}%) in {Path} — " +
                    "these will NOT be auto-imported. Use the File Scan page to review and accept them manually.",
                    below.Count, detect ? "per type" : threshold, folder.Path);

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

            return new FolderPreview(folder, importGroups, fileCount, below.Count, bundle);
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
