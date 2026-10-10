using System.Collections.Concurrent;
using Chronicle.Core.Models;
using Chronicle.Data;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Central scheduler that drives all registered IScheduledTask implementations
/// and all plugin-owned background tasks stored in the DB.
/// Ticks every 30 seconds, fires tasks whose next_run_at has elapsed,
/// and prevents concurrent execution of the same task by any trigger path.
/// </summary>
public sealed class TaskSchedulerService : BackgroundService, ITaskSchedulerService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    // How soon a plugin's own "fetch-missing-metadata" task is retried when it finishes a run
    // still owing Pending enrichment rows, instead of waiting for its normal (often once-daily)
    // cron. See ComputeNextRunAtAsync's own doc for the full reasoning.
    private static readonly TimeSpan FetchMissingRetryInterval = TimeSpan.FromMinutes(15);
    private const string FetchMissingMetadataTaskId = "fetch-missing-metadata";

    private readonly IReadOnlyList<IScheduledTask> _tasks;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, bool> _running = new();
    private readonly ILogger _log = Log.ForContext<TaskSchedulerService>();

    public TaskSchedulerService(
        IEnumerable<IScheduledTask> tasks,
        IServiceScopeFactory scopeFactory)
    {
        _tasks = tasks.ToList();
        _scopeFactory = scopeFactory;
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Information("TaskSchedulerService starting with {Count} system task(s)", _tasks.Count);
        await SeedTasksAsync(stoppingToken);
        await SkipMissedRunsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error(ex, "TaskSchedulerService: unhandled error in tick");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── ITaskSchedulerService ─────────────────────────────────────────────────

    public bool IsRunning(string taskId) => _running.ContainsKey(taskId);

    public async Task<TriggerResult> TriggerNowAsync(
        string taskId, CancellationToken ct = default)
    {
        // Guard against concurrent execution
        if (!_running.TryAdd(taskId, true))
            return TriggerResult.AlreadyRunning;

        // Look up the row — required for both run-result tracking and plugin routing
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var taskRow = await db.BackgroundTasks.FindAsync(taskId);

        if (taskRow is null)
        {
            _running.TryRemove(taskId, out _);
            return TriggerResult.NotFound;
        }

        // For system tasks verify there is an IScheduledTask implementation
        if (taskRow.PluginId is null)
        {
            var systemTask = _tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (systemTask is null)
            {
                _running.TryRemove(taskId, out _);
                return TriggerResult.NotFound;
            }
        }

        _ = Task.Run(() => RunTaskAsync(taskRow, CancellationToken.None), CancellationToken.None);
        return TriggerResult.Started;
    }

    // ── Internals (internal for unit tests) ──────────────────────────────────

    internal async Task SeedTasksAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        foreach (var task in _tasks)
        {
            var existing = await db.BackgroundTasks
                .FirstOrDefaultAsync(t => t.TaskId == task.TaskId, ct);
            if (existing is not null) continue;

            var nextRun = GetNextOccurrence(task.DefaultCron);
            db.BackgroundTasks.Add(new BackgroundTask
            {
                TaskId         = task.TaskId,
                DisplayName    = task.DisplayName,
                Description    = task.Description,
                CronExpression = task.DefaultCron,
                IsEnabled      = true,
                NextRunAt      = nextRun
            });

            _log.Information("TaskScheduler: seeded task '{TaskId}' with cron '{Cron}'",
                task.TaskId, task.DefaultCron);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Restarting Chronicle must not start work. A task whose next run time passed while the app was down (or while it
    /// was restarting) is moved on to its next cron time instead of firing the moment the scheduler starts; tasks run
    /// only when their cron says so, or when someone presses Run Now. Before this, every restart began a full library
    /// scan, duplicate cleanup and every plugin's sync at once, which left the server unresponsive for minutes.
    /// </summary>
    internal async Task SkipMissedRunsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var now = DateTime.UtcNow;

        var changed = false;
        var missed = await db.BackgroundTasks
            .Where(t => t.IsEnabled && t.NextRunAt != null && t.NextRunAt <= now)
            .ToListAsync(ct);
        foreach (var row in missed)
        {
            var next = GetNextOccurrence(row.CronExpression);
            if (next is null) continue;   // an unreadable cron stays as it was; nothing to schedule
            _log.Information("TaskScheduler: '{TaskId}' was due at {Due:u} while Chronicle was not running; skipping to {Next:u}",
                row.TaskId, row.NextRunAt, next);
            row.NextRunAt = next;
            changed = true;
        }

        // A task that has a schedule but was never given a next run time would never start: plugin tasks used to be added
        // without one, so a daily "fetch missing" did nothing until someone ran it by hand once. Give each its next cron time.
        var unscheduled = await db.BackgroundTasks
            .Where(t => t.IsEnabled && t.NextRunAt == null && t.CronExpression != "")
            .ToListAsync(ct);
        foreach (var row in unscheduled)
        {
            if (GetNextOccurrence(row.CronExpression) is not { } next) continue;
            _log.Information("TaskScheduler: '{TaskId}' had no next run time; scheduled for {Next:u} (cron {Cron})", row.TaskId, next, row.CronExpression);
            row.NextRunAt = next;
            changed = true;
        }

        if (changed) await db.SaveChangesAsync(ct);
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var now = DateTime.UtcNow;

        var dueRows = await db.BackgroundTasks
            .Where(t => t.IsEnabled && t.NextRunAt != null && t.NextRunAt <= now)
            .ToListAsync(ct);

        foreach (var row in dueRows)
        {
            if (!_running.TryAdd(row.TaskId, true))
            {
                _log.Warning("TaskScheduler: '{TaskId}' is already running — skipping scheduled fire", row.TaskId);
                row.NextRunAt = GetNextOccurrence(row.CronExpression);
                continue;
            }

            // For system tasks, verify an IScheduledTask registration exists
            if (row.PluginId is null)
            {
                var task = _tasks.FirstOrDefault(t => t.TaskId == row.TaskId);
                if (task is null)
                {
                    _log.Warning("TaskScheduler: no IScheduledTask found for DB row '{TaskId}'", row.TaskId);
                    _running.TryRemove(row.TaskId, out _);
                    continue;
                }
            }

            row.NextRunAt = GetNextOccurrence(row.CronExpression);
            _ = Task.Run(() => RunTaskAsync(row, CancellationToken.None), CancellationToken.None);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task RunTaskAsync(BackgroundTask row, CancellationToken ct)
    {
        _log.Information("TaskScheduler: starting '{TaskId}'", row.TaskId);
        var startedAt = DateTime.UtcNow;

        try
        {
            if (row.PluginId is not null)
            {
                // Plugin-owned task: route through IPluginTaskRunner resolved from scope
                using var scope = _scopeFactory.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<IPluginTaskRunner>();
                var bareTaskId = row.TaskId.Contains(':')
                    ? row.TaskId[(row.TaskId.IndexOf(':') + 1)..]
                    : row.TaskId;
                await runner.RunAsync(row.PluginId, bareTaskId, ct);

                // Per-user direction (2026-09-28): the Hardcover book-series reconcile pass runs
                // as its own step chained right after Hardcover's own fetch-missing-metadata task,
                // rather than on its own cron or inline per-book the way movie collections do it --
                // there's nothing for it to reconcile until that pass has attached/refreshed the
                // series ids it reads. This is a deliberate one-off special case for this one
                // plugin/task pair, not a general task-dependency mechanism.
                if (string.Equals(row.PluginId, "hardcover", StringComparison.OrdinalIgnoreCase) &&
                    bareTaskId == FetchMissingMetadataTaskId)
                {
                    var reconcile = scope.ServiceProvider.GetRequiredService<HardcoverSeriesReconcileService>();
                    await reconcile.ExecuteAsync(ct);
                }
            }
            else
            {
                // System task: look up IScheduledTask registration
                var task = _tasks.FirstOrDefault(t => t.TaskId == row.TaskId);
                if (task is null)
                {
                    _log.Warning("TaskSchedulerService: no IScheduledTask registered for '{TaskId}'", row.TaskId);
                    return;
                }
                await task.ExecuteAsync(ct);
            }

            await PersistRunResultAsync(row.TaskId, startedAt, succeeded: true, error: null);
            _log.Information("TaskScheduler: '{TaskId}' completed successfully in {Elapsed:F1}s",
                row.TaskId, (DateTime.UtcNow - startedAt).TotalSeconds);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "TaskScheduler: '{TaskId}' failed", row.TaskId);
            await PersistRunResultAsync(row.TaskId, startedAt, succeeded: false, error: ex.Message, exception: ex);
        }
        finally
        {
            _running.TryRemove(row.TaskId, out _);
        }
    }

    private async Task PersistRunResultAsync(
        string taskId, DateTime lastRunAt, bool succeeded, string? error, Exception? exception = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var row = await db.BackgroundTasks.FindAsync(taskId);
            if (row is null) return;

            row.LastRunAt        = lastRunAt;
            row.LastRunSucceeded = succeeded;
            row.LastErrorMessage = succeeded ? null : error;
            row.NextRunAt        = await ComputeNextRunAtAsync(db, row, succeeded);

            await db.SaveChangesAsync();

            // Tell the administrators a task broke, so nobody has to open the Background Tasks page to find out, but only
            // when it is something they can act on. A bug, or a plugin built for another version of Chronicle that has no
            // update to install, is logged and shown on the Background Tasks page, not put in the bell.
            // One unread notice per task: a task failing every few minutes does not bury the bell.
            if (!succeeded)
            {
                var notifier = scope.ServiceProvider.GetService<Notifications.INotificationService>();
                var kind = Notifications.TaskFailureTriage.Classify(exception);
                if (kind == Notifications.TaskFailureKind.Fixable)
                {
                    await (notifier?.NotifyAdminsAsync(
                        Chronicle.Core.Models.NotificationKinds.TaskFailed,
                        $"{row.DisplayName} failed",
                        string.IsNullOrWhiteSpace(error) ? "The task stopped with an error." : error,
                        "/settings/background-tasks",
                        dedupeKey: $"task:{taskId}") ?? Task.FromResult(0));
                }
                else if (kind == Notifications.TaskFailureKind.PluginIncompatible)
                {
                    var plugin = string.IsNullOrEmpty(row.PluginId) ? null
                        : await db.Plugins.AsNoTracking().FirstOrDefaultAsync(p => p.PluginId == row.PluginId);
                    if (plugin?.LatestVersionAvailable is { Length: > 0 } latest)
                        await (notifier?.NotifyAdminsAsync(
                            Chronicle.Core.Models.NotificationKinds.TaskFailed,
                            $"{plugin.Name} needs an update",
                            $"This version of {plugin.Name} does not work with this version of Chronicle. Update it to v{latest} on the Plugins page.",
                            "/plugins",
                            dedupeKey: $"task:{taskId}") ?? Task.FromResult(0));
                    else
                        _log.Warning("TaskScheduler: '{TaskId}' failed because its plugin is not compatible with this Chronicle and no update is available; not announced in the notification bell", taskId);
                }
                else
                {
                    _log.Warning("TaskScheduler: '{TaskId}' failed with an internal error; not announced in the notification bell", taskId);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "TaskScheduler: failed to persist run result for '{TaskId}'", taskId);
        }
    }

    /// <summary>
    /// Normally just the cron's own next occurrence -- EXCEPT for a plugin's own
    /// "fetch-missing-metadata" task that finished this run still owing Pending enrichment
    /// rows for that plugin. MetadataEnrichmentService.EnrichPendingAsync's own while(true) loop
    /// never stops early for any reason OTHER than the provider becoming unavailable (rate
    /// limit or network failure exhausting ProviderCallGuard's own bounded retries) -- there is
    /// no arbitrary batch cap that could otherwise explain Pending rows remaining after a
    /// completed (non-throwing) run. So "still Pending right after this task just ran" reliably
    /// means "got blocked", not merely "big queue, more to do eventually."
    ///
    /// Root-caused live (2026-09-15): TMDB's own fetch-missing-metadata cron is once daily —
    /// on a 270,000+ item backlog that kept getting interrupted by transient network blips
    /// (see ProviderCallGuard's own doc), waiting for the NEXT cron occurrence meant at most one
    /// blocked, barely-progressing attempt per day. Per-user request: "I would really prefer
    /// that it keep trying periodically until the queues are emptied." Retries again in
    /// <see cref="FetchMissingRetryInterval"/> instead of the full cron interval, repeating for
    /// as many runs in a row as it takes -- each successful PersistRunResultAsync call re-checks
    /// Pending and keeps scheduling itself soon again -- until the queue genuinely is empty, at
    /// which point this falls back to the task's normal cron cadence exactly as before. Never
    /// mutates the stored CronExpression itself, only this one run's computed NextRunAt, so nothing
    /// needs to "restore" the normal schedule afterward.
    ///
    /// Never schedules LATER than the plugin's own normal cron would have -- a plugin whose cron
    /// is already more frequent than the retry interval (e.g. MusicBrainz's "0 */4 * * *") must
    /// keep its own faster cadence, not get slowed down to this floor.
    /// </summary>
    private async Task<DateTime?> ComputeNextRunAtAsync(ChronicleDbContext db, BackgroundTask row, bool succeeded)
    {
        var normalNext = GetNextOccurrence(row.CronExpression);

        // A genuine unhandled failure (not the graceful provider-unavailable pause, which never
        // surfaces as succeeded: false -- see this method's own doc) gets the normal cadence,
        // not a rapid retry loop against whatever just crashed it.
        if (!succeeded || row.PluginId is null) return normalNext;

        var bareTaskId = row.TaskId.Contains(':') ? row.TaskId[(row.TaskId.IndexOf(':') + 1)..] : row.TaskId;
        if (bareTaskId != FetchMissingMetadataTaskId) return normalNext;

        var stillPending = await db.MediaEnrichments
            .AnyAsync(e => e.PluginId == row.PluginId && e.Status == EnrichmentStatus.Pending);
        if (!stillPending) return normalNext;

        var soonRetry = DateTime.UtcNow + FetchMissingRetryInterval;
        return normalNext is { } n && n < soonRetry ? n : soonRetry;
    }

    /// <summary>The next time a cron expression fires, in UTC; null for an empty or unreadable expression.</summary>
    internal static DateTime? GetNextOccurrence(string cronExpression)
    {
        try
        {
            var expr = CronExpression.Parse(cronExpression);
            return expr.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);
        }
        catch
        {
            return null;
        }
    }
}
