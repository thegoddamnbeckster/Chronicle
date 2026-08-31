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
    /// Short hint shown in the Fix Match panel explaining what the user should enter.
    /// Example: "Enter a TMDB ID (e.g. 550), typed ID (movie:550 · tv:1396), or URL"
    /// Optional — falls back to "Enter an ID or URL to search {Name}" if absent.
    /// </summary>
    [JsonPropertyName("fixMatchHint")]
    public string? FixMatchHint { get; set; }

    /// <summary>
    /// Background tasks this plugin wants Chronicle to schedule.
    /// Omit entirely if the plugin has no scheduled work.
    /// </summary>
    [JsonPropertyName("background_tasks")]
    public List<PluginTaskManifest>? BackgroundTasks { get; set; }

    /// <summary>
    /// How many enrichment items MetadataEnrichmentService.EnrichPendingAsync may process
    /// concurrently for this plugin, each on its own DB scope. Defaults to 1 (today's
    /// existing strictly-sequential behaviour) -- a plugin only needs to raise this if it has
    /// no rate limiter of its own AND its upstream API can genuinely take concurrent requests
    /// (e.g. TMDB, which has no self-imposed throttle at all). Raising this for a plugin that
    /// already enforces its own request-interval limiter (e.g. Wikipedia's WikipediaRateLimiter,
    /// a single shared gate) buys nothing -- every concurrent worker just queues up behind that
    /// same gate, so such plugins should leave this at the default rather than set it. Per-user
    /// request (2026-08-30): "is it possible to have multiple instances or threads working
    /// against each one?" -- yes, but only worth it where the plugin itself declares it's safe.
    /// </summary>
    [JsonPropertyName("max_enrichment_concurrency")]
    public int MaxEnrichmentConcurrency { get; set; } = 1;
}
