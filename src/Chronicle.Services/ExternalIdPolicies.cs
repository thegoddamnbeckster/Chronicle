using System.Text.RegularExpressions;
using Chronicle.Plugins.Models;

namespace Chronicle.Services;

/// <summary>
/// The external-id policies the installed plugins declared. Chronicle names no provider; everything it needs to know about how
/// a provider's ids behave comes from here, loaded when plugins load. A source nobody declared a policy for is treated
/// plainly (see <see cref="ExternalIdPolicy"/>).
/// </summary>
public static class ExternalIdPolicies
{
    private sealed record Snapshot(IReadOnlyDictionary<string, ExternalIdPolicy> BySource);

    private static volatile Snapshot _snapshot = Build([]);

    // Tests run in parallel; each can describe its own plugins without touching the others.
    private static readonly AsyncLocal<Snapshot?> _scoped = new();
    private static Snapshot Current => _scoped.Value ?? _snapshot;

    private static Snapshot Build(IEnumerable<ExternalIdPolicy> policies)
    {
        var map = new Dictionary<string, ExternalIdPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in policies)
            if (!string.IsNullOrWhiteSpace(p.Source)) map[p.Source.Trim()] = p;
        return new Snapshot(map);
    }

    public static void Load(IEnumerable<ExternalIdPolicy> policies) => _snapshot = Build(policies);

    public static void LoadForCurrentContext(IEnumerable<ExternalIdPolicy> policies) => _scoped.Value = Build(policies);

    public static ExternalIdPolicy? Find(string? source) =>
        string.IsNullOrWhiteSpace(source) ? null : Current.BySource.GetValueOrDefault(source);

    // ── identity ────────────────────────────────────────────────────────────────

    /// <summary>True when two items disagreeing on this source proves nothing about whether they are the same item.</summary>
    public static bool IgnoredForConflicts(string? source) => Find(source)?.IgnoredForConflicts == true;

    /// <summary>True when this source's id comes from the item's name rather than an independent identity.</summary>
    public static bool IsNameDerived(string? source) => Find(source)?.NameDerived == true;

    /// <summary>The (plugin id, partition field) pairs whose titles can carry a trailing disambiguation parenthetical.</summary>
    public static (string PluginId, string Field)[] DisambiguatedTitleFields() =>
        Current.BySource.Values.Where(p => p.DisambiguatedTitleField is { Length: > 0 })
            .Select(p => (p.PluginId, p.DisambiguatedTitleField!)).ToArray();

    /// <summary>The plugin id of the first name-derived provider ("" when none is installed).</summary>
    public static string NameDerivedPluginId() =>
        Current.BySource.Values.Where(p => p.NameDerived).Select(p => p.PluginId).FirstOrDefault() ?? string.Empty;

    /// <summary>The source of the first name-derived provider ("" when none is installed).</summary>
    public static string NameDerivedSource() => NameDerivedSources().FirstOrDefault() ?? string.Empty;

    public static string[] NameDerivedSources() =>
        Current.BySource.Values.Where(p => p.NameDerived).Select(p => p.Source).ToArray();

    // ── how a raw id is filed ───────────────────────────────────────────────────

    /// <summary>
    /// The (source, stored id) a raw id is filed under. An id carrying a provider's shared prefix goes to that provider's
    /// row, prefix removed. Otherwise it belongs to <paramref name="callerSource"/>, with that provider's stored-prefix
    /// rewrites applied. Null when neither applies (no caller, no recognised prefix).
    /// </summary>
    public static (string Source, string Id)? Normalize(string rawId, string? callerSource)
    {
        foreach (var p in Current.BySource.Values)
        {
            if (p.SharedRawIdPrefix is not { Length: > 0 } prefix || !rawId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var bare = rawId[prefix.Length..];
            if (p.KeepSharedPrefixWhenIdStarts.Any(k => bare.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
                bare = prefix + bare;
            return (p.Source, bare);
        }

        if (callerSource is null) return null;

        var id = rawId;
        if (Find(callerSource) is { } own)
            foreach (var (from, to) in own.StoredPrefixRewrites)
                if (id.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                {
                    id = to + id[from.Length..];
                    break;
                }
        return (callerSource, id);
    }

    /// <summary>True when the provider declared this id unusable and it must not be stored.</summary>
    public static bool IsRefused(string? source, string id) =>
        Find(source)?.RefusedIdPattern is { Length: > 0 } pattern && Regex.IsMatch(id, pattern, RegexOptions.IgnoreCase);

    // ── cross references ────────────────────────────────────────────────────────

    /// <summary>The stored form of a source's id found inside another provider's cross-reference block, or null. The first of
    /// <paramref name="kinds"/> (most specific first: "movie", the media type's name) the provider has a format for wins.</summary>
    public static string? FormatCrossRef(string source, string value, bool isNumber, IEnumerable<string?> kinds)
    {
        var p = Find(source);
        if (p is { CrossRefValueKinds.Length: > 0 } &&
            !p.CrossRefValueKinds.Contains(isNumber ? "number" : "string", StringComparer.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrEmpty(value)) return null;

        string? format = null;
        if (p is not null)
        {
            foreach (var kind in kinds)
                if (kind is not null && p.CrossRefFormats.TryGetValue(kind, out format)) break;
            format ??= p.CrossRefFormats.GetValueOrDefault("default");
        }
        return (format ?? "{source}:{id}").Replace("{source}", source).Replace("{id}", value);
    }
}
