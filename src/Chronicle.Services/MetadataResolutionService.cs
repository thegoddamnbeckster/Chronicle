using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class MetadataResolutionService(
    AssignmentConfigCache configCache,
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataResolutionService> logger) : IMetadataResolutionService
{
    private const int BatchSize = 100;

    // Maps assignment config snake_case field names → camelCase keys used in metadata_json plugin blobs
    internal static readonly Dictionary<string, string> FieldMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["title"]           = "title",
            ["overview"]        = "overview",
            ["year"]            = "year",
            ["poster_url"]      = "posterUrl",
            ["backdrop_url"]    = "backdropUrl",
            ["runtime_minutes"] = "runtimeMinutes",
            ["rating"]          = "rating",
            ["genres"]          = "genres",
            ["cast"]            = "cast",
            ["directors"]       = "directors",
            ["tags"]            = "tags",
        };

    public async Task ResolveAsync(MediaItem item, ChronicleDbContext db, CancellationToken ct = default)
    {
        var mediaTypeName = (item.MediaType?.Name ?? string.Empty).ToLowerInvariant();

        var priorityMap = await configCache.GetForTypeAsync(mediaTypeName, item.HierarchyLevel, ct);

        var blobs = ParsePluginBlobs(item.MetadataJson);
        blobs.Remove("_resolved"); // remove stale before recomputing

        var resolved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (assignmentField, jsonKey) in FieldMap)
        {
            if (!priorityMap.TryGetValue(assignmentField, out var plugins) || plugins.Count == 0)
                continue;

            foreach (var pluginId in plugins)
            {
                if (!blobs.TryGetValue(pluginId, out var blob)) continue;
                if (blob.ValueKind != JsonValueKind.Object) continue;
                if (!blob.TryGetProperty(jsonKey, out var val)) continue;
                if (!HasValue(val)) continue;
                resolved[jsonKey] = val;
                break;
            }
        }

        blobs["_resolved"] = JsonSerializer.SerializeToElement(resolved);
        item.MetadataJson  = JsonSerializer.Serialize(blobs);

        // Promote first-class columns
        if (resolved.TryGetValue("posterUrl", out var poster) && HasValue(poster))
            item.PosterUrl = poster.GetString();
        if (resolved.TryGetValue("overview", out var ov) && HasValue(ov))
            item.Overview = ov.GetString();
        if (resolved.TryGetValue("runtimeMinutes", out var rt) && rt.ValueKind == JsonValueKind.Number)
            item.RuntimeMinutes = rt.GetInt32();

        // title and year only promoted at level 0
        if (item.HierarchyLevel == 0)
        {
            if (resolved.TryGetValue("title", out var title) && HasValue(title))
                item.Name = title.GetString()!;
            if (resolved.TryGetValue("year", out var yr) && yr.ValueKind == JsonValueKind.Number)
                item.Year = yr.GetInt32();
        }

        logger.LogDebug("Resolved {Count} fields for item {Id} ({Name})", resolved.Count, item.Id, item.Name);
    }

    public Task ResolveAllForMediaTypeAsync(string mediaTypeName, CancellationToken ct = default)
        => throw new NotImplementedException();

    /// Parses metadata_json into a mutable dictionary keyed by plugin ID.
    internal static Dictionary<string, JsonElement> ParsePluginBlobs(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson) ?? [];
        }
        catch (JsonException) { return []; }
    }

    /// Returns true when a JsonElement has a meaningful (non-empty) value.
    internal static bool HasValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null      => false,
        JsonValueKind.Undefined => false,
        JsonValueKind.String    => !string.IsNullOrWhiteSpace(el.GetString()),
        JsonValueKind.Array     => el.GetArrayLength() > 0,
        _                       => true,
    };
}
