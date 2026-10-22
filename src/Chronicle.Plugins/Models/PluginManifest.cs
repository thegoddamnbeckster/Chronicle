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
    /// Names of the media types this plugin handles (for example "movies", "tv", "music"), so the plugin catalog can be
    /// filtered to "plugins for this type". Optional; a plugin without it is listed under the tags it was catalogued with.
    /// </summary>
    [JsonPropertyName("supported_media_types")]
    public List<string>? SupportedMediaTypes { get; set; }

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
    /// How to turn this plugin's stored external id into the web page it resolved to, so a person can open it and look.
    /// Rules are tried in order; the first whose <see cref="PluginSourceUrlRule.Match"/> regex matches the id wins, and its
    /// <see cref="PluginSourceUrlRule.Url"/> has $0 (whole id) and $1..$n (capture groups) substituted. Optional; with none,
    /// the card simply has no link. Example: { "match": "^movie:(\d+)", "url": "https://www.themoviedb.org/movie/$1" }.
    /// </summary>
    [JsonPropertyName("sourceUrls")]
    public List<PluginSourceUrlRule>? SourceUrls { get; set; }

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

/// <summary>One id-pattern to web-address rule of a plugin manifest's <c>sourceUrls</c>.</summary>
public class PluginSourceUrlRule
{
    /// <summary>Regular expression tested against the stored external id.</summary>
    [JsonPropertyName("match")]
    public string Match { get; set; } = string.Empty;

    /// <summary>Address template; $0 is the whole id, $1..$n are the regex's capture groups.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
