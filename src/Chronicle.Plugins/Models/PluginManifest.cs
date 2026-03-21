using System.Text.Json.Serialization;

namespace Chronicle.Plugins.Models;

/// <summary>
/// Describes a plugin. Loaded from <c>manifest.json</c> alongside the plugin DLL.
/// Plugin authors must ship a <c>manifest.json</c> next to their DLL.
/// </summary>
public class PluginManifest
{
    /// <summary>Unique reverse-domain identifier, e.g. "chronicle.plugin.tmdb".</summary>
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Minimum Chronicle version required to run this plugin.</summary>
    [JsonPropertyName("min_chronicle_version")]
    public string MinChronicleVersion { get; set; } = "1.0.0";

    /// <summary>
    /// Fully-qualified type name of the class implementing one of the plugin interfaces.
    /// e.g. "Chronicle.Plugins.TMDB.TMDBMetadataProvider"
    /// </summary>
    [JsonPropertyName("entry_type")]
    public string EntryType { get; set; } = string.Empty;

    /// <summary>
    /// URL of the plugin's icon (typically the favicon of the service's website).
    /// Chronicle's UI displays this icon on the Plugins page. Optional.
    /// Example: "https://trakt.tv/favicon.ico"
    /// </summary>
    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    /// <summary>
    /// Hex accent colour for Chronicle's light-mode UI (e.g. "#BA478F").
    /// Used as the task card border and tinted background on the Background Tasks page.
    /// Falls back to Chronicle's default accent if absent.
    /// </summary>
    [JsonPropertyName("brandColorLight")]
    public string? BrandColorLight { get; set; }

    /// <summary>
    /// Hex accent colour for Chronicle's dark-mode UI (e.g. "#CF6BAA").
    /// Provide a colour that is visible on a dark background.
    /// Falls back to Chronicle's default accent if absent.
    /// </summary>
    [JsonPropertyName("brandColorDark")]
    public string? BrandColorDark { get; set; }

    /// <summary>
    /// Background tasks this plugin wants Chronicle to schedule.
    /// Omit entirely if the plugin has no scheduled work.
    /// </summary>
    [JsonPropertyName("background_tasks")]
    public List<PluginTaskManifest>? BackgroundTasks { get; set; }
}
