namespace Chronicle.Core.Models;

public class BackgroundTask
{
    public string TaskId           { get; set; } = string.Empty;
    public string DisplayName      { get; set; } = string.Empty;
    public string Description      { get; set; } = string.Empty;
    public string CronExpression   { get; set; } = string.Empty;
    public bool   IsEnabled        { get; set; } = true;
    public DateTime? LastRunAt     { get; set; }
    public bool?  LastRunSucceeded { get; set; }
    public string? LastErrorMessage{ get; set; }
    public DateTime? NextRunAt     { get; set; }

    /// <summary>
    /// The plugin that owns this task, or null for system tasks.
    /// Populated from the plugin's manifest.json background_tasks declaration.
    /// </summary>
    public string? PluginId { get; set; }

    /// <summary>
    /// When false the task cannot be scheduled via cron — it must be triggered manually.
    /// Defaults to true for backward compatibility.
    /// </summary>
    public bool Schedulable { get; set; } = true;

    /// <summary>
    /// Optional title shown in a confirmation dialog before the task is run manually.
    /// Null means no confirmation is required.
    /// </summary>
    public string? RunConfirmationTitle   { get; set; }

    /// <summary>
    /// Optional body text shown in the confirmation dialog before the task is run manually.
    /// </summary>
    public string? RunConfirmationMessage { get; set; }

    /// <summary>Navigation property — loaded via Include in queries that need branding.</summary>
    public Plugin? Plugin { get; set; }
}
