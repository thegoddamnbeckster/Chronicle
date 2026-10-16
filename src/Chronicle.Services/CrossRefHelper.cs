using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Plugins.Models;

namespace Chronicle.Services;

/// <summary>
/// Shared utilities for extracting and formatting cross-reference IDs from plugin ExtendedData.
/// Used by FileScanService (on add) and MetadataEnrichmentService (post-enrichment cascade).
/// </summary>
internal static class CrossRefHelper
{
    /// <summary>
    /// Extracts all cross-reference IDs from <paramref name="meta"/>.ExtendedData.ids,
    /// skipping <paramref name="fromSource"/> (the plugin that produced the metadata).
    /// Returns (shortSource, formattedExternalId) pairs ready for enrichment row seeding.
    /// </summary>
    internal static List<(string Source, string Id)> ExtractCrossRefIds(
        MediaMetadata meta, string fromSource, string? mediaTypeName = null)
    {
        var result = new List<(string, string)>();
        if (meta.ExtendedData is not { } ext) return result;
        if (ext.ValueKind != JsonValueKind.Object) return result;
        if (!ext.TryGetProperty("ids", out var ids)) return result;
        if (ids.ValueKind != JsonValueKind.Object) return result;

        // Which kind of item the ids describe: the media type's own family decides whether it is movie-like; an id the
        // producing provider says can only denote a movie (ExternalIdPolicy.FamilyIdPatterns) says so when the type is not
        // known. The type's name is passed on so a provider that writes ids differently for a type it distinguishes (it
        // declares that in its ExternalIdPolicy) can.
        bool isMovie = MediaTypeRegistry.IsMovieLike(mediaTypeName)
                    || ExternalIdPolicies.FamilyOfId(fromSource, meta.ExternalId) == ProviderFamilies.Movie;

        foreach (var prop in ids.EnumerateObject())
        {
            var key = prop.Name.ToLowerInvariant();
            if (key == fromSource) continue;

            var formatted = FormatCrossRefId(key, prop.Value, isMovie, mediaTypeName);
            if (formatted is not null)
                result.Add((key, formatted));
        }

        return result;
    }

    /// <summary>
    /// Formats a raw ID value into the external-ID string expected by the target plugin, as that plugin's own
    /// <see cref="Chronicle.Core.Models.ExternalIdPolicy"/> says (plainly "source:value" when it declared none).
    /// Returns null when the value is not an id.
    /// </summary>
    internal static string? FormatCrossRefId(string source, JsonElement value, bool isMovie, string? mediaTypeName = null)
    {
        string? text;
        bool isNumber;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number: text = value.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture); isNumber = true; break;
            case JsonValueKind.String: text = value.GetString(); isNumber = false; break;
            default: return null;
        }
        return text is { Length: > 0 }
            ? ExternalIdPolicies.FormatCrossRef(source, text, isNumber, [isMovie ? "movie" : null, mediaTypeName?.ToLowerInvariant()])
            : null;
    }
}
