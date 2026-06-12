using System.Text.Json;
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

        bool isMovie = meta.ExternalId?.Contains(":movie:", StringComparison.OrdinalIgnoreCase) == true
                    || meta.ExternalId?.StartsWith("movie:", StringComparison.OrdinalIgnoreCase) == true;
        // isAnime is derived from Chronicle's media type name, not SIMKL's own classification.
        // Items stored under Chronicle's "tv" type that SIMKL classifies as anime will still
        // receive simkl:tv:{id} here. Only items with an explicit "anime" media type benefit.
        bool isAnime = string.Equals(mediaTypeName, "anime", StringComparison.OrdinalIgnoreCase);

        foreach (var prop in ids.EnumerateObject())
        {
            var key = prop.Name.ToLowerInvariant();
            if (key == fromSource) continue;

            var formatted = FormatCrossRefId(key, prop.Value, isMovie, isAnime);
            if (formatted is not null)
                result.Add((key, formatted));
        }

        return result;
    }

    /// <summary>
    /// Formats a raw ID value into the external-ID string expected by the target plugin.
    /// Returns null when the value type is wrong or the source is unrecognised.
    /// </summary>
    internal static string? FormatCrossRefId(string source, JsonElement value, bool isMovie, bool isAnime = false)
    {
        switch (source)
        {
            case "tmdb":
                if (value.ValueKind == JsonValueKind.Number)
                    return $"{(isMovie ? "movie" : "tv")}:{value.GetInt64()}";
                break;

            case "imdb":
                if (value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 0 } imdb)
                    return $"imdb:{imdb}";
                break;

            case "trakt":
                if (value.ValueKind == JsonValueKind.Number)
                    return $"trakt:{(isMovie ? "movie" : "show")}:{value.GetInt64()}";
                break;

            case "simkl":
                if (value.ValueKind == JsonValueKind.Number)
                {
                    var simklType = isMovie ? "movie" : (isAnime ? "anime" : "tv");
                    return $"simkl:{simklType}:{value.GetInt64()}";
                }
                break;

            default:
                if (value.ValueKind == JsonValueKind.Number)
                    return $"{source}:{value.GetInt64()}";
                if (value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 0 } s)
                    return $"{source}:{s}";
                break;
        }
        return null;
    }
}
