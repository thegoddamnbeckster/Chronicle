namespace Chronicle.Core.Helpers;

/// <summary>
/// Utilities for working with Chronicle plugin IDs.
/// </summary>
public static class PluginIdHelper
{
    /// <summary>
    /// Returns the short source name derived from a full plugin ID.
    /// <list type="bullet">
    ///   <item><c>"chronicle.plugin.tmdb"</c> → <c>"tmdb"</c></item>
    ///   <item><c>"chronicle.plugin.trakt"</c> → <c>"trakt"</c></item>
    ///   <item><c>"hardcover"</c> → <c>"hardcover"</c></item>
    /// </list>
    /// This is the canonical "source" key used in <c>media_external_ids.Source</c>
    /// and <c>media_enrichment.PluginId</c> short-form lookups.
    /// </summary>
    public static string ToSource(string pluginId)
    {
        var dot = pluginId.LastIndexOf('.');
        return dot >= 0 ? pluginId[(dot + 1)..] : pluginId;
    }
}
