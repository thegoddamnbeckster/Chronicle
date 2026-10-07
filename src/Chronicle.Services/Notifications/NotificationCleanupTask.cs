using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services.Notifications
{
    /// <summary>Removes notifications older than <c>notifications.retain_days</c> (default 90) so the table cannot grow without bound.</summary>
    public sealed class NotificationCleanupTask : IScheduledTask
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger _log = Log.ForContext<NotificationCleanupTask>();

        public NotificationCleanupTask(IServiceScopeFactory scopes) => _scopes = scopes;

        public string TaskId => "notifications_cleanup";
        public string DisplayName => "Notification Cleanup";
        public string Description => "Removes notifications older than the retention period (90 days by default).";
        public string DefaultCron => "30 4 * * *";

        public async Task ExecuteAsync(CancellationToken ct)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == NotificationService.RetainDaysKey, ct);
            var days = row is not null && int.TryParse(row.Value, out var d) && d is >= 1 and <= 3650 ? d : NotificationService.DefaultRetainDays;

            var removed = await scope.ServiceProvider.GetRequiredService<INotificationService>().PurgeOlderThanAsync(TimeSpan.FromDays(days), ct);
            if (removed > 0) _log.Information("Removed {Count} notification(s) older than {Days} days", removed, days);
        }
    }
}
