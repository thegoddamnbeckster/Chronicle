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
    /// Ids of retired tasks this one took over. A newly seeded task starts disabled when every retired row that exists was
    /// disabled (so merging tasks never switches back on something an administrator had turned off), and takes the
    /// schedule of the first such row. Empty for a task that replaces nothing.
    /// </summary>
    IReadOnlyList<string> ReplacesTaskIds => [];

    /// <summary>
    /// True for a heavy database writer (the backup, duplicate cleanup, the integrity repair, a library scan). An exclusive
    /// task conflicts with every other task: of two that conflict, the higher-ranked one runs and the other pauses until it
    /// is done (see <see cref="TaskPriorityGate"/>). Light tasks do not conflict with one another.
    /// </summary>
    bool Exclusive => false;

    /// <summary>
    /// Execute one run of this task.
    /// Called by TaskSchedulerService on schedule and on manual trigger.
    /// </summary>
    Task ExecuteAsync(CancellationToken ct);
}
