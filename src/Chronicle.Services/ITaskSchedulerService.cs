namespace Chronicle.Services;

public enum TriggerResult
{
    Started,
    AlreadyRunning,
    NotFound
}

/// <summary>
/// Exposes scheduler operations to the API layer without coupling to BackgroundService.
/// </summary>
public interface ITaskSchedulerService
{
    /// <summary>Returns true if the given task is currently executing.</summary>
    bool IsRunning(string taskId);

    /// <summary>
    /// Fires the task immediately if it is not already running.
    /// Returns Started, AlreadyRunning, or NotFound.
    /// </summary>
    Task<TriggerResult> TriggerNowAsync(string taskId, CancellationToken ct = default);
}
