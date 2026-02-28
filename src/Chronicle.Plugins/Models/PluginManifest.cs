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
}
