using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services.Database
{
    // Two scheduled tasks, so they show up on Settings -> Background Tasks with last/next run and a
    // Run Now button without any extra storage. Each is a no-op (logged) on a database the app cannot
    // back up, rather than a recurring error.

    /// <summary>
    /// The nightly database housekeeping job: back up, check the size, refresh statistics, and prune old
    /// notifications. One job rather than three, since they are all small, all about the same database, and
    /// the backup should come first. The full rebuild stays its own task because it locks the database.
    /// </summary>
    public sealed class DatabaseBackupTask : IScheduledTask
    {
        private readonly IDatabaseAdminService _db;
        private readonly IServiceScopeFactory? _scopes;
        private readonly ILogger _log = Log.ForContext<DatabaseBackupTask>();
        public DatabaseBackupTask(IDatabaseAdminService db, IServiceScopeFactory? scopes = null)
        {
            _db = db;
            _scopes = scopes;
        }

        public string TaskId => "database_backup";
        public string DisplayName => "Database Backup & Maintenance";
        public string Description => "Nightly: takes a zipped backup (removing the oldest beyond the retention count), warns if the database has grown large, refreshes query statistics and trims the write-ahead log, and removes notifications older than the retention period.";
        public string DefaultCron => "0 2 * * *";

        public async Task ExecuteAsync(CancellationToken ct)
        {
            // Independent steps: a failing backup (a full disk) must not also stop the statistics refresh and the notification
            // purge, which used to be tasks of their own. The task still fails at the end so the failure is recorded and notified.
            var steps = new PassFailures(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "Database backup and maintenance");
            var backedUp = false;
            await steps.RunAsync("backup", async t => backedUp = await BackUpAsync(t), ct);
            if (backedUp)
            {
                await steps.RunAsync("size check", CheckSizeAsync, ct);
                await steps.RunAsync("maintenance", RunLightMaintenanceAsync, ct);
            }
            await steps.RunAsync("notification purge", PurgeNotificationsAsync, ct);
            steps.ThrowIfAny();
        }

        private async Task RunLightMaintenanceAsync(CancellationToken ct)
        {
            try { await _db.RunMaintenanceAsync(heavy: false, ct); }
            catch (DatabaseAdminException ex) when (ex.Code == "UNSUPPORTED")
            {
                _log.Information("Database maintenance skipped: {Reason}", ex.Message);
            }
        }

        /// <summary>False when the database cannot be backed up (so cannot be maintained either).</summary>
        private async Task<bool> BackUpAsync(CancellationToken ct)
        {
            try { await _db.CreateBackupAsync(BackupKinds.Scheduled, ct); return true; }
            catch (DatabaseAdminException ex) when (ex.Code == "UNSUPPORTED")
            {
                _log.Information("Database backup skipped: {Reason}", ex.Message);
                return false;
            }
        }

        // The nightly run is also the daily check on how big the database has become.
        private async Task CheckSizeAsync(CancellationToken ct)
        {
            if (_scopes is null) return;
            var status = await _db.GetStatusAsync(ct);
            if (status.Supported && status.OverWarnSize)
            {
                using var scope = _scopes.CreateScope();
                await (scope.ServiceProvider.GetService<Notifications.INotificationService>()?.NotifyAdminsAsync(
                    NotificationKinds.DatabaseSize,
                    $"The database is {DatabaseAdminService.Format(status.DatabaseBytes + status.WalBytes)}",
                    $"That is over the {DatabaseAdminService.Format(status.WarnSizeBytes)} warning size. A full rebuild can reclaim free space.",
                    "/settings/database", dedupeKey: "database-size", ct: ct) ?? Task.FromResult(0));
            }
        }

        /// <summary>Removes notifications older than <c>notifications.retain_days</c> (default 90) so the table cannot grow without bound.</summary>
        private async Task PurgeNotificationsAsync(CancellationToken ct)
        {
            if (_scopes is null) return;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Notifications.NotificationService.RetainDaysKey, ct);
            var days = row is not null && int.TryParse(row.Value, out var d) && d is >= 1 and <= 3650 ? d : Notifications.NotificationService.DefaultRetainDays;

            var notifications = scope.ServiceProvider.GetService<Notifications.INotificationService>();
            if (notifications is null) return;
            var removed = await notifications.PurgeOlderThanAsync(TimeSpan.FromDays(days), ct);
            if (removed > 0) _log.Information("Removed {Count} notification(s) older than {Days} days", removed, days);
        }
    }

    public sealed class DatabaseHeavyMaintenanceTask : IScheduledTask
    {
        private readonly IDatabaseAdminService _db;
        private readonly ILogger _log = Log.ForContext<DatabaseHeavyMaintenanceTask>();
        public DatabaseHeavyMaintenanceTask(IDatabaseAdminService db) => _db = db;

        public string TaskId => "database_maintenance_full";
        public string DisplayName => "Database Maintenance (full rebuild)";
        public string Description => "Rebuilds indexes and compacts the database file to reclaim free space. Locks the database while it runs, and needs free disk space about the size of the database.";
        public string DefaultCron => "0 4 1 * *";

        public async Task ExecuteAsync(CancellationToken ct)
        {
            try { await _db.RunMaintenanceAsync(heavy: true, ct); }
            catch (DatabaseAdminException ex) when (ex.Code is "UNSUPPORTED" or "LOW_DISK")
            {
                // A full disk is a reason to skip this run, not to fail every month; the reason is logged.
                _log.Warning("Database full rebuild skipped: {Reason}", ex.Message);
            }
        }
    }
}
