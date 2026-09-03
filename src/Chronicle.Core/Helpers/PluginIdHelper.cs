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
    /// whether the dictionary key itself is the short or full plugin ID), then falls back to
    /// comparing the SHORT FORM of the dictionary key itself against the caller's short source
    /// -- so "chronicle.plugin.wikipedia" still matches a caller that only passed "wikipedia",
    /// even for a blob with no internal "source" property of its own (the legacy flat-format
    /// shape). Never matches the reserved "_resolved" or "_overrides" keys.
    ///
    /// Confirmed live (2026-09-03): matching ONLY by dictionary key silently removed nothing
    /// when a caller passed the short source name for a blob stored under the full plugin ID
    /// key -- left a person item resolving a DIFFERENT real person's Wikipedia bio and photo
    /// indefinitely after what looked like a successful "clear match". The exact-key-only
    /// fallback that first fixed that (comparing the dict key against `pluginIdOrSource` and
    /// `shortSource` verbatim) still missed a legacy no-"source"-property blob stored under the
    /// FULL key when the caller passed the SHORT form -- caught in code review the same day --
    /// so the fallback now derives and compares the key's own short form instead of relying on
    /// the caller and the key happening to use the same naming convention.
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
                          string.Equals(ToSource(kv.Key), shortSource, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => kv.Key)
            .ToList();
    }
}
