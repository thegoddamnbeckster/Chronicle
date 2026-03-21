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

    /// <summary>Default 5-field UTC cron expression (e.g. "0 4 * * *" = 4 am daily).</summary>
    [JsonPropertyName("default_cron")]
    public string DefaultCron { get; set; } = string.Empty;

    /// <summary>Whether the task is enabled when first installed. Defaults to true.</summary>
    [JsonPropertyName("default_enabled")]
    public bool DefaultEnabled { get; set; } = true;
}
