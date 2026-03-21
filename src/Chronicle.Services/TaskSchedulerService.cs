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
            await PersistRunResultAsync(row.TaskId, startedAt, succeeded: false, error: ex.Message);
        }
        finally
        {
            _running.TryRemove(row.TaskId, out _);
        }
    }

    private async Task PersistRunResultAsync(
        string taskId, DateTime lastRunAt, bool succeeded, string? error)
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
            row.NextRunAt        = GetNextOccurrence(row.CronExpression);

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "TaskScheduler: failed to persist run result for '{TaskId}'", taskId);
        }
    }

    private static DateTime? GetNextOccurrence(string cronExpression)
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
