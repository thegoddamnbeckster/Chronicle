using Chronicle.Core.Models.Scan;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Scheduled task that scans all enabled scan folders nightly and
/// auto-imports groups whose confidence score meets the configured threshold.
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

        using var scope = _scopeFactory.CreateScope();
        var fileScanSvc    = scope.ServiceProvider.GetRequiredService<IFileScanService>();
        var scanFolderSvc  = scope.ServiceProvider.GetRequiredService<IScanFolderService>();
        var db             = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var allFolders = await scanFolderSvc.GetAllAsync(ct);
        var folders    = allFolders.Where(f => f.IsEnabled).ToList();

        if (folders.Count == 0)
        {
            _log.Information("ScheduledScanService: No enabled scan folders configured");
            return;
        }

        var threshold = await fileScanSvc.GetConfidenceThresholdAsync(ct);
        _log.Information(
            "ScheduledScanService: Found {Count} enabled scan folder(s), confidence threshold: {Threshold}",
            folders.Count, threshold);

        // Resolve the admin user to own the imported library entries.
        var adminUser = await db.Users
            .Where(u => u.IsAdmin)
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(ct);

        if (adminUser is null)
        {
            _log.Warning("ScheduledScanService: No admin user found — cannot import groups");
            return;
        }

        foreach (var folder in folders)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                _log.Information(
                    "ScheduledScanService: Scanning {Path} ({MediaType})",
                    folder.Path,
                    folder.MediaType?.DisplayName ?? "unknown type");

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
                    continue;
                }

                _log.Information(
                    "ScheduledScanService: Auto-importing {Count} group(s) from {Path}",
                    passingGroups.Count, folder.Path);

                var importRequest = new ImportGroupsRequest(
                    passingGroups.Select(g => ToImport(g)).ToList(),
                    folder.MediaTypeId);

                var summary = await fileScanSvc.ImportGroupsAsync(importRequest, adminUser.Id, ct);

                _log.Information(
                    "ScheduledScanService: Import complete for {Path} — imported: {Imported}, failed: {Failed}, duplicates: {Duplicates}",
                    folder.Path, summary.Imported, summary.Failed, summary.Duplicates);

                var dbFolder = await db.ScanFolders.FindAsync([folder.Id], ct);
                if (dbFolder is not null)
                {
                    dbFolder.LastScannedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error scanning folder {Path}", folder.Path);
            }
        }

        _log.Information("ScheduledScanService: Scheduled scan complete");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ScanGroupImport ToImport(ScanGroup group) => new(
        group.Name,
        group.Year,
        group.PosterPath,
        group.Children.Select(c => ToImport(c)).ToList(),
        group.Files);
}
