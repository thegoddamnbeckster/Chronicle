using Chronicle.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services.Database
{
    // Three scheduled tasks, so they show up on Settings -> Background Tasks with last/next run and a
    // Run Now button without any extra storage. Each is a no-op (logged) on a database the app cannot
    // back up, rather than a recurring error.

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
        public string DisplayName => "Database Backup";
        public string Description => "Takes a consistent zipped backup of the database and removes the oldest beyond the retention count.";
        public string DefaultCron => "0 2 * * *";

        public async Task ExecuteAsync(CancellationToken ct)
        {
            try { await _db.CreateBackupAsync(BackupKinds.Scheduled, ct); }
            catch (DatabaseAdminException ex) when (ex.Code == "UNSUPPORTED")
            {
                _log.Information("Database backup skipped: {Reason}", ex.Message);
                return;
            }

            // The nightly run is also the daily check on how big the database has become.
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
    }

    public sealed class DatabaseLightMaintenanceTask : IScheduledTask
    {
        private readonly IDatabaseAdminService _db;
        private readonly ILogger _log = Log.ForContext<DatabaseLightMaintenanceTask>();
        public DatabaseLightMaintenanceTask(IDatabaseAdminService db) => _db = db;

        public string TaskId => "database_maintenance_light";
        public string DisplayName => "Database Maintenance (quick)";
        public string Description => "Refreshes query statistics and trims the write-ahead log. Quick and safe to run any time.";
        public string DefaultCron => "30 3 * * 0";

        public async Task ExecuteAsync(CancellationToken ct)
        {
            try { await _db.RunMaintenanceAsync(heavy: false, ct); }
            catch (DatabaseAdminException ex) when (ex.Code == "UNSUPPORTED")
            {
                _log.Information("Database maintenance skipped: {Reason}", ex.Message);
            }
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
