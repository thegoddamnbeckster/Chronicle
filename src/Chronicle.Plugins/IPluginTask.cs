namespace Chronicle.Plugins;

/// <summary>
/// Implement this interface in your plugin assembly to provide a custom
/// background task. Chronicle discovers implementations automatically when
/// the plugin loads and wires them to the task declared in manifest.json
/// via matching <c>TaskId</c>.
///
/// Only needed for custom task IDs. The well-known IDs
/// "fetch-missing-metadata" and "resync-all-metadata" are handled
/// internally by Chronicle — no implementation required.
/// </summary>
public interface IPluginTask
{
    /// <summary>
    /// Must exactly match the <c>task_id</c> value declared in manifest.json
    /// (without the plugin-ID prefix Chronicle adds internally).
    /// </summary>
    string TaskId { get; }

    /// <summary>Perform the task's work. Called on the declared schedule.</summary>
    Task RunAsync(CancellationToken ct);
}
