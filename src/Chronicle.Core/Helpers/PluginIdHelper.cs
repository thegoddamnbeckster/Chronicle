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

    /// <summary>
    /// Removes every blob belonging to <paramref name="pluginIdOrSource"/> from a MetadataJson
    /// document (via <see cref="FindProviderBlobKeys"/>) and returns the updated JSON, with
    /// <paramref name="removed"/> reporting whether anything was actually there to remove.
    /// Returns <paramref name="metadataJson"/> unchanged (removed=false) when it is null/blank,
    /// isn't valid JSON, or has no blob for that provider.
    ///
    /// Shared by every caller that strips a provider's stale data so a match that no longer
    /// backs an item can never keep contributing its old title/overview/poster via
    /// MetadataResolutionService's priority walk -- originally only MediaController.
    /// ClearExternalId (a user explicitly clearing a match) did this; MetadataEnrichmentService's
    /// own automatic match-rejection path did not, so a row that flipped Completed -&gt; NotFound
    /// (e.g. a later pass discovering the matched id now belongs to a different item) left its
    /// already-merged blob sitting in place, no longer backed by any valid ExternalId, still
    /// eligible to win MetadataResolutionService's resolution walk. Confirmed live (2026-09-28):
    /// "Sing" (2016) kept displaying Sing 2's Wikipedia plot summary for days after Wikipedia's
    /// own enrichment row was rejected to NotFound, because nothing had ever removed the blob.
    /// </summary>
    public static string? RemoveProviderBlob(string? metadataJson, string pluginIdOrSource, out bool removed)
    {
        removed = false;
        if (string.IsNullOrWhiteSpace(metadataJson)) return metadataJson;
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson);
            if (root is null) return metadataJson;
            var keys = FindProviderBlobKeys(root, pluginIdOrSource);
            if (keys.Count == 0) return metadataJson;
            foreach (var key in keys) root.Remove(key);
            removed = true;
            return JsonSerializer.Serialize(root);
        }
        catch (JsonException)
        {
            return metadataJson; // malformed JSON — leave as-is
        }
    }
}
