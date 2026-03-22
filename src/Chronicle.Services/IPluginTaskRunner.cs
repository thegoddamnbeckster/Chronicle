namespace Chronicle.Services;

/// <summary>
/// Routes background task execution for installed plugins.
/// Handles well-known task IDs internally and dispatches custom IDs
/// to the plugin's own <c>IPluginTask</c> implementation.
/// </summary>
public interface IPluginTaskRunner
{
    /// <summary>
    /// Executes the background task identified by <paramref name="taskId"/>
    /// on behalf of <paramref name="pluginId"/>.
    /// </summary>
    Task RunAsync(string pluginId, string taskId, CancellationToken ct);
}
