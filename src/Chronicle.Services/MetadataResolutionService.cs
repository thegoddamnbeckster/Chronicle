using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class MetadataResolutionService(
    AssignmentConfigCache configCache,
    FieldAliasCache fieldAliasCache,
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataResolutionService> logger) : IMetadataResolutionService
{
    private const int BatchSize = 100;

    // Maps assignment config snake_case field names → the canonical camelCase key used in
    // metadata_json plugin blobs and to store the resolved value. This defines the SET of
    // canonical fields (a schema decision — adding a new one still needs a code change), not
    // which alias key names count as a match for it — that part is admin-configurable via
    // FieldAliasCache (metadata_field_aliases.config), merged in at resolve time below, so
    // e.g. "label" can also match "recordLabel" or "publisher" without editing this file.
    internal static readonly Dictionary<string, string[]> FieldMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["title"]           = ["title"],
            ["overview"]        = ["overview"],
            ["year"]            = ["year"],
            ["poster_url"]      = ["posterUrl"],
            ["backdrop_url"]    = ["backdropUrl"],
            ["runtime_minutes"] = ["runtimeMinutes"],
            ["rating"]          = ["rating"],
            ["genres"]          = ["genres"],
            ["cast"]            = ["cast"],
            ["directors"]       = ["directors"],
            ["tags"]            = ["tags"],
            // Artwork fields — populated by supplementary providers such as Fanart.tv
            ["logo_url"]        = ["logoUrl"],
            ["banner_url"]      = ["bannerUrl"],
            ["thumb_url"]       = ["thumbUrl"],
            ["clearart_url"]    = ["clearartUrl"],
            ["disc_url"]        = ["discUrl"],
            ["character_art_url"] = ["characterArtUrl"],
            // Collection grouping — any plugin that writes belongsToCollection in its blob
            // may be configured here to control which plugin's collection data takes precedence.
            ["collection"]      = ["belongsToCollection"],
            // Music-relevant fields.
            ["composer"]        = ["composer"],
            ["label"]           = ["label"],
            ["bpm"]             = ["bpm"],
            ["mood"]            = ["mood"],
            ["language"]        = ["language"],
            ["isrc"]            = ["isrc"],
        };

    public IReadOnlyCollection<string> GetCanonicalFields() => FieldMap.Keys;

    public async Task ResolveAsync(MediaItem item, ChronicleDbContext db, CancellationToken ct = default)
    {
        var mediaTypeName = (item.MediaType?.Name ?? string.Empty).ToLowerInvariant();

        var priorityMap  = await configCache.GetForTypeAsync(mediaTypeName, item.HierarchyLevel, ct);
        var extraAliases = await fieldAliasCache.GetAllAsync(ct);

        var blobs = ParsePluginBlobs(item.MetadataJson);
        blobs.Remove("_resolved"); // remove stale before recomputing

        var resolved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (assignmentField, baseKeys) in FieldMap)
        {
            var canonicalKey = baseKeys[0];
            string[] jsonKeys = extraAliases.TryGetValue(assignmentField, out var extras) && extras.Count > 0
                ? [.. baseKeys, .. extras]
                : baseKeys;
            if (priorityMap.TryGetValue(assignmentField, out var plugins) && plugins.Count > 0)
            {
                // Use the configured priority order for this field.
                foreach (var pluginId in plugins)
                {
                    if (!blobs.TryGetValue(pluginId, out var blob)) continue;
                    if (blob.ValueKind != JsonValueKind.Object) continue;
                    if (!TryGetBlobPropertyAny(blob, jsonKeys, out var val)) continue;
                    if (!HasValue(val)) continue;
                    resolved[canonicalKey] = val;
                    break;
                }
            }
            else
            {
                // Field not explicitly configured — fall back to first blob that has a value.
                foreach (var blob in blobs.Values)
                {
                    if (blob.ValueKind != JsonValueKind.Object) continue;
                    if (!TryGetBlobPropertyAny(blob, jsonKeys, out var val)) continue;
                    if (!HasValue(val)) continue;
                    resolved[canonicalKey] = val;
                    break;
                }
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

    public async Task ResolveAllForMediaTypeAsync(string mediaTypeName, CancellationToken ct = default)
    {
        logger.LogInformation("Starting bulk _resolved recompute for media type '{Type}'", mediaTypeName);
        int lastId = 0, totalDone = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            var batch = await db.MediaItems
                .Include(m => m.MediaType)
                .Where(m => m.MediaType!.Name == mediaTypeName && m.Id > lastId)
                .OrderBy(m => m.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            foreach (var item in batch)
            {
                try { await ResolveAsync(item, db, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { logger.LogWarning(ex, "Bulk resolve failed for item {Id}", item.Id); }
            }

            await db.SaveChangesAsync(ct);
            lastId     = batch[^1].Id;
            totalDone += batch.Count;
            logger.LogDebug("Bulk resolve: {Done} items processed for '{Type}'", totalDone, mediaTypeName);
        }

        logger.LogInformation("Bulk _resolved recompute complete: {Total} items for '{Type}'", totalDone, mediaTypeName);
    }

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

    /// Looks up a property in a plugin blob, falling back to the nested extendedData object.
    /// Some plugins (e.g. TMDB) store extra fields like belongsToCollection under extendedData
    /// rather than at the top level of their blob.
    internal static bool TryGetBlobProperty(JsonElement blob, string key, out JsonElement value)
    {
        if (blob.TryGetProperty(key, out value)) return true;
        if (blob.TryGetProperty("extendedData", out var ext) &&
            ext.ValueKind == JsonValueKind.Object &&
            ext.TryGetProperty(key, out value)) return true;
        value = default;
        return false;
    }

    /// Tries each alias in turn against a single blob, returning the first one present
    /// (via TryGetBlobProperty, including its extendedData fallback). Lets differently-named
    /// fields from different sources (e.g. "label" vs. "recordLabel") resolve to one canonical value.
    internal static bool TryGetBlobPropertyAny(JsonElement blob, IReadOnlyList<string> keys, out JsonElement value)
    {
        foreach (var key in keys)
        {
            if (TryGetBlobProperty(blob, key, out value)) return true;
        }
        value = default;
        return false;
    }
}
