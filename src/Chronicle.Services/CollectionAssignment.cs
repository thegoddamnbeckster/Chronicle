using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins.Models;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

/// <summary>
/// Collection-level metadata assignment: which source wins for the data of a COLLECTION (a movie collection, a video game series) as
/// opposed to the items inside it. A movie collection and a game series are different things from different sources (TMDB, IGDB, and
/// whatever turns up next), and no source's order may decide another's, so each flat, collection-supporting type has its own card
/// ("Movies Collections", "Video Games Collections") stored under the key "{type}.collection" in the assignment config.
///
/// A source can be ordered here when it declares the "collection" field for the type (TMDB and IGDB both do) or says it supports
/// collections. Until a collection field has an order of its own it follows the type's order, so nothing changes for anyone who
/// never opens the card.
/// </summary>
public static class CollectionAssignment
{
    public const string KeySuffix = ".collection";

    /// <summary>The fields a collection can have, in the order the card lists them. Only those some collection source declares are offered.</summary>
    private static readonly string[] FieldNames =
        ["backdrop_url", "banner_url", "logo_url", "overview", "poster_url", "tags", "thumb_url", "title"];

    public static readonly string[] DefaultFields = ["backdrop_url", "overview", "poster_url", "tags", "title"];

    public static string KeyFor(string typeName) => typeName + KeySuffix;

    public static bool IsKey(string key) => key.EndsWith(KeySuffix, StringComparison.OrdinalIgnoreCase);

    public static string TypeOf(string key) => IsKey(key) ? key[..^KeySuffix.Length] : key;

    /// <summary>Only a flat type is grouped into collections (a one-level type that supports them): movies, anime movies, fan edits, games.</summary>
    public static bool AppliesTo(MediaType type) => type.SupportsCollections && type.HierarchyLevels == 1;

    /// <summary>Whether a source can supply collection data for its type.</summary>
    public static bool CanDescribeCollections(MediaTypeSupport support) =>
        support.SupportsCollections || support.SupportedFields.Contains("collection", StringComparer.OrdinalIgnoreCase);

    /// <summary>The fields the type's collection card offers: those a collection source declares, or a sensible default when none does.</summary>
    public static string[] FieldsFor(IEnumerable<MediaTypeSupport> supportForTheType)
    {
        var declared = supportForTheType
            .Where(CanDescribeCollections)
            .SelectMany(s => s.SupportedFields)
            .Append("tags")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fields = FieldNames.Where(declared.Contains).ToArray();
        return fields.Length > 1 ? fields : DefaultFields;
    }

    public static string DisplayName(string typeDisplayName) => $"{typeDisplayName} Collections";

    /// <summary>
    /// A collection card with no saved order starts from the type's own saved order for the same fields, which is what the resolver
    /// would use anyway, so the page shows what is actually in effect and saving it makes it explicit.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string[]>> SeedFromTypeOrder(
        Dictionary<string, Dictionary<string, string[]>> assignments, IReadOnlyDictionary<string, string[]> assignableFields)
    {
        var result = new Dictionary<string, Dictionary<string, string[]>>(assignments, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, fields) in assignableFields)
        {
            if (!IsKey(key) || result.ContainsKey(key)) continue;
            if (!result.TryGetValue(TypeOf(key), out var typeOrder)) continue;
            var seeded = typeOrder
                .Where(f => fields.Contains(f.Key, StringComparer.OrdinalIgnoreCase) && f.Value.Length > 0)
                .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
            if (seeded.Count > 0) result[key] = seeded;
        }
        return result;
    }

    /// <summary>
    /// Whether an item is a collection (a container holding other items) rather than one of the items in it. The same two tests the
    /// enrichment sweep uses: it carries a "collection:" id, or it is a top-level item of a collection-supporting flat type with children.
    /// </summary>
    public static async Task<bool> IsCollectionAsync(ChronicleDbContext db, MediaItem item, CancellationToken ct = default)
    {
        if (item.Id == 0 || item.HierarchyLevel != 0 || item.MediaType is not { } type || !AppliesTo(type)) return false;
        return await db.MediaExternalIds.AnyAsync(e => e.MediaItemId == item.Id && e.ExternalId.StartsWith("collection:"), ct)
            || await db.MediaItems.AnyAsync(c => c.ParentId == item.Id, ct);
    }
}
