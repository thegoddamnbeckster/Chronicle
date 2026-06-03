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

    public Task ResolveAsync(MediaItem item, ChronicleDbContext db, CancellationToken ct = default)
        => throw new NotImplementedException();

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
