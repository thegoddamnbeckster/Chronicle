namespace Chronicle.Core.Models;

public class UserPreferences
{
    public bool? ShowDiagnostics { get; set; }
    public bool? DefaultFoldsOpen { get; set; }
    /// <summary>
    /// Per-fold open/closed state. Keys: "media.{id}.{pluginId}", "backgroundTasks.{pluginId}".
    /// Values: true = open, false = closed.
    /// </summary>
    public Dictionary<string, bool>? Folds { get; set; }
}
