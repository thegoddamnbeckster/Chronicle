using System.Text.Json;

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

    /// <summary>
    /// Finds every top-level key in a parsed MetadataJson dictionary that belongs to the given
    /// provider `source` (short form, e.g. "wikipedia") -- used by MediaController.
    /// ClearExternalId to strip a provider's stale blob when its external ID is removed.
    /// Matches by each blob's OWN internal "source" property first (reliable regardless of
    /// whether the dictionary key itself is the short or full plugin ID), then falls back to a
    /// direct key match against both the full plugin ID and the short source name, for a blob
    /// that carries no "source" property of its own. Never matches the reserved "_resolved" or
    /// "_overrides" keys.
    ///
    /// Confirmed live (2026-09-03): matching ONLY by dictionary key silently removed nothing
    /// when a caller passed the short source name for a blob stored under the full plugin ID
    /// key -- left a person item resolving a DIFFERENT real person's Wikipedia bio and photo
    /// indefinitely after what looked like a successful "clear match".
    /// </summary>
    public static List<string> FindProviderBlobKeys(
        IReadOnlyDictionary<string, JsonElement> blobs, string pluginIdOrSource)
    {
        var shortSource = ToSource(pluginIdOrSource);

        return blobs
            .Where(kv => kv.Key is not ("_resolved" or "_overrides") &&
                         ((kv.Value.ValueKind == JsonValueKind.Object &&
                           kv.Value.TryGetProperty("source", out var src) &&
                           string.Equals(src.GetString(), shortSource, StringComparison.OrdinalIgnoreCase)) ||
                          string.Equals(kv.Key, pluginIdOrSource, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(kv.Key, shortSource, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => kv.Key)
            .ToList();
    }
}
