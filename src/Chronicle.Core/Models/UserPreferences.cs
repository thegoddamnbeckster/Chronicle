namespace Chronicle.Core.Models;

public class UserPreferences
{
    public bool? ShowDiagnostics { get; set; }
    public bool? DefaultFoldsOpen { get; set; }
    /// <summary>
    /// When true (default), stub MediaItems for movies missing from owned collections are visible
    /// in the user's library so they can track what to watch next.
    /// When false, stubs are hidden from all library views.
    /// </summary>
    public bool? CreateCollectionStubs { get; set; }
    /// <summary>
    /// Per-fold open/closed state. Keys: "media.{id}.{pluginId}", "backgroundTasks.{pluginId}".
    /// Values: true = open, false = closed.
    /// </summary>
    public Dictionary<string, bool>? Folds { get; set; }
}
