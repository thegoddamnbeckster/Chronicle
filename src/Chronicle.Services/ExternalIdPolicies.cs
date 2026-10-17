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

    private static ExternalIdPolicy? FirstNameDerived() =>
        Current.BySource.Values.Where(p => p.NameDerived).OrderBy(p => p.Source, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>The name-derived provider's birth-year tag pattern, or null.</summary>
    public static Regex? BirthYearTag() => Compile(FirstNameDerived()?.BirthYearTagPattern);

    /// <summary>The name-derived provider's pattern for pages that are not about one item, or null.</summary>
    public static Regex? NotAnEntityTitle() => Compile(FirstNameDerived()?.NotAnEntityTitlePattern);

    // A plugin supplies these patterns, so one that does not parse, or that runs away, must cost only its own rule, never the
    // enrichment pass that asked. Compiled once per pattern.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex?> Compiled = new();

    private static Regex? Compile(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        return Compiled.GetOrAdd(pattern, p =>
        {
            try { return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException) { return null; }
        });
    }

    private static bool IsMatch(string pattern, string input)
    {
        try { return Compile(pattern)?.IsMatch(input) == true; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static Match? Match(string pattern, string input)
    {
        try { return Compile(pattern)?.Match(input); }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>The plugin id of the first name-derived provider ("" when none is installed).</summary>
    public static string NameDerivedPluginId() =>
        Current.BySource.Values.Where(p => p.NameDerived).OrderBy(p => p.Source, StringComparer.Ordinal).Select(p => p.PluginId).FirstOrDefault() ?? string.Empty;

    /// <summary>The source of the first name-derived provider ("" when none is installed).</summary>
    public static string NameDerivedSource() => NameDerivedSources().FirstOrDefault() ?? string.Empty;

    public static string[] NameDerivedSources() =>
        Current.BySource.Values.Where(p => p.NameDerived).Select(p => p.Source).OrderBy(x => x, StringComparer.Ordinal).ToArray();

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
        Find(source)?.RefusedIdPattern is { Length: > 0 } pattern && IsMatch(pattern, id);

    // ── is this id plausible for this item ──────────────────────────────────────

    /// <summary>What a provider's declared id shapes say about one stored id.</summary>
    public readonly record struct IdAssessment(bool Recognized, bool Valid, bool EmbedsParentId)
    {
        public static readonly IdAssessment Trusted = new(false, true, false);
    }

    /// <summary>
    /// Whether <paramref name="externalId"/>, which <paramref name="source"/> issued, can sit on an item at this depth of the
    /// hierarchy whose type is in <paramref name="itemFamily"/>. An id of no declared shape is trusted as it is.
    /// </summary>
    public static IdAssessment Assess(string? source, string? externalId, int depth, string? itemFamily, string? parentFamily)
    {
        if (string.IsNullOrEmpty(externalId) || Find(source) is not { IdShapes.Length: > 0 } policy) return IdAssessment.Trusted;

        var recognized = false;
        var valid = false;
        var embeds = false;
        foreach (var shape in policy.IdShapes)
        {
            if (!IsMatch(shape.Pattern, externalId)) continue;
            recognized = true;
            embeds |= shape.EmbedsParentId;
            if (shape.Depths.Contains(depth)
                && (shape.ItemFamilies.Length == 0 || (itemFamily is not null && shape.ItemFamilies.Contains(itemFamily, StringComparer.OrdinalIgnoreCase)))
                && (shape.ParentFamilies.Length == 0 || (parentFamily is not null && shape.ParentFamilies.Contains(parentFamily, StringComparer.OrdinalIgnoreCase))))
                valid = true;
        }
        return recognized ? new IdAssessment(true, valid, embeds) : IdAssessment.Trusted;
    }

    // ── ids of lower items, positions, kinds ────────────────────────────────────

    /// <summary>
    /// The id of an item <paramref name="depth"/> levels below a top-level item whose id is <paramref name="rootId"/>, as the
    /// provider's own templates build it (null when the provider declared none that fits, or a number the template needs is missing).
    /// </summary>
    public static string? DeriveChildId(string? source, string rootId, int depth, int? number, int? parentNumber)
    {
        if (Find(source) is not { } policy) return null;
        foreach (var t in policy.ChildIds.Where(c => c.Depth == depth))
        {
            var m = Match(t.RootIdPattern, rootId);
            if (m is not { Success: true }) continue;
            if ((t.Template.Contains("{n}") && number is null) || (t.Template.Contains("{parentN}") && parentNumber is null)) continue;
            return t.Template
                .Replace("{root}", m.Groups["root"].Value)
                .Replace("{n}", number?.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{parentN}", parentNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return null;
    }

    /// <summary>The (season, episode) a provider's id names, by the pattern the provider declared; null when it names none.</summary>
    public static (int Season, int Episode)? PositionInId(string? source, string? externalId)
    {
        if (string.IsNullOrEmpty(externalId) || Find(source)?.PositionInIdPattern is not { Length: > 0 } pattern) return null;
        var m = Match(pattern, externalId);
        return m is { Success: true } && int.TryParse(m.Groups["season"].Value, out var s) && int.TryParse(m.Groups["episode"].Value, out var e) ? (s, e) : null;
    }

    /// <summary>The first position found in any of the ids (each tagged with its source).</summary>
    public static (int Season, int Episode)? PositionInAnyId(IEnumerable<(string Source, string ExternalId)> ids) =>
        ids.Select(i => PositionInId(i.Source, i.ExternalId)).FirstOrDefault(p => p is not null);

    /// <summary>The provider family an id can only denote ("movie"), by the patterns the provider declared; null when it says none.</summary>
    public static string? FamilyOfId(string? source, string? externalId)
    {
        if (string.IsNullOrEmpty(externalId) || Find(source) is not { } policy) return null;
        foreach (var (family, pattern) in policy.FamilyIdPatterns)
            if (IsMatch(pattern, externalId)) return family;
        return null;
    }

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
