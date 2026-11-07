using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Services;

/// <summary>
/// The owner's Metadata Assignment precedence for one media type, field by field ("title": [tmdb, imdb, ...], "poster_url": [...]).
/// Used where several providers' answers are combined into one card before an item exists (the Add Media search), so a card shows
/// the same value for each field that the item will have once it is added.
///
/// A provider the owner did not list for a field is not dropped: it ranks after the listed ones, so a field that only an unlisted
/// provider has still reaches the card. A field with no list at all keeps the order the providers were given in.
/// </summary>
internal sealed class FieldPrecedence
{
    private readonly Dictionary<string, List<string>> _byField;

    private FieldPrecedence(Dictionary<string, List<string>> byField) => _byField = byField;

    public static FieldPrecedence For(string? configJson, string typeName)
    {
        var byField = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(configJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.EnumerateObject().FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                        is { Value.ValueKind: JsonValueKind.Object } type)
                {
                    foreach (var field in type.Value.EnumerateObject())
                    {
                        if (field.Value.ValueKind != JsonValueKind.Array) continue;
                        byField[field.Name] = field.Value.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(e.GetString()))
                            .Select(e => e.GetString()!)
                            .ToList();
                    }
                }
            }
            catch (JsonException) { /* an unreadable setting means no precedence: the given order stands */ }
        }
        return new FieldPrecedence(byField);
    }

    /// <summary>The value of <paramref name="field"/> from the highest-ranked member that has one, or the default when none does.</summary>
    public T? Pick<T>(
        IReadOnlyList<(IMetadataProvider Provider, ScoredCandidate Candidate)> members,
        string field,
        Func<ScoredCandidate, T> selector,
        Func<T, bool> hasValue)
    {
        _byField.TryGetValue(field, out var listed);
        int Rank(string pluginId)
        {
            if (listed is null) return 0;
            var i = listed.FindIndex(p => string.Equals(p, pluginId, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? listed.Count : i;
        }

        foreach (var (_, candidate) in members
                     .Select((m, i) => (m, i))
                     .OrderBy(x => Rank(x.m.Provider.PluginId))
                     .ThenBy(x => x.i)
                     .Select(x => x.m))
        {
            var value = selector(candidate);
            if (hasValue(value)) return value;
        }
        return default;
    }
}
