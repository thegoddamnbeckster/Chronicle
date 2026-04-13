using System.Text.Json.Serialization;

namespace Chronicle.Plugins.Models;

/// <summary>
/// Describes a single background task declared by a plugin in its manifest.json.
/// </summary>
public class PluginTaskManifest
{
    /// <summary>
    /// Identifies the task type. Use a well-known ID ("fetch-missing-metadata",
    /// "resync-all-metadata") to get Chronicle's built-in execution, or supply a
    /// custom ID and implement <c>IPluginTask</c> in your plugin assembly.
    /// </summary>
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Human-readable name shown as the card heading in the UI.</summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional subtitle shown below the heading.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Default 5-field UTC cron expression (e.g. "0 4 * * *" = 4 am daily). Null means no default schedule.</summary>
    [JsonPropertyName("default_cron")]
    public string? DefaultCron { get; set; }

    /// <summary>Whether the task is enabled when first installed. Defaults to true.</summary>
    [JsonPropertyName("default_enabled")]
    public bool DefaultEnabled { get; set; } = true;

    /// <summary>
    /// When false the task cannot be auto-scheduled — it must be triggered manually.
    /// Defaults to true.
    /// </summary>
    [JsonPropertyName("schedulable")]
    public bool Schedulable { get; set; } = true;

    /// <summary>Flat title for a run-confirmation dialog (populated from run_confirmation.title).</summary>
    public string? RunConfirmationTitle   { get; set; }

    /// <summary>Flat body for a run-confirmation dialog (populated from run_confirmation.message).</summary>
    public string? RunConfirmationMessage { get; set; }

    /// <summary>
    /// Optional confirmation dialog shown before the user can trigger the task manually.
    /// Setting this property populates <see cref="RunConfirmationTitle"/> and
    /// <see cref="RunConfirmationMessage"/>.
    /// </summary>
    [JsonPropertyName("run_confirmation")]
    public PluginTaskRunConfirmation? RunConfirmation
    {
        get => RunConfirmationTitle is null ? null
            : new PluginTaskRunConfirmation { Title = RunConfirmationTitle, Message = RunConfirmationMessage ?? string.Empty };
        set
        {
            RunConfirmationTitle   = value?.Title;
            RunConfirmationMessage = value?.Message;
        }
    }
}

/// <summary>Describes the confirmation dialog shown before a manually-triggered plugin task.</summary>
public class PluginTaskRunConfirmation
{
    [JsonPropertyName("title")]
    public string Title   { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
