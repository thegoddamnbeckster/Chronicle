namespace Chronicle.Services;

/// <summary>
/// Marker interface for a task managed by TaskSchedulerService.
/// Register implementations as IScheduledTask in DI to have them
/// automatically discovered, seeded into the DB, and scheduled.
/// </summary>
public interface IScheduledTask
{
    /// <summary>Unique stable key stored in background_tasks.task_id.</summary>
    string TaskId { get; }

    /// <summary>Human-readable name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>One-sentence description shown under the task name in the UI.</summary>
    string Description { get; }

    /// <summary>5-field cron expression used to seed the DB on first startup.</summary>
    string DefaultCron { get; }

    /// <summary>
    /// Execute one run of this task.
    /// Called by TaskSchedulerService on schedule and on manual trigger.
    /// </summary>
    Task ExecuteAsync(CancellationToken ct);
}
