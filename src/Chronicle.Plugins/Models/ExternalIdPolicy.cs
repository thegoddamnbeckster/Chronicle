namespace Chronicle.Plugins.Models;

/// <summary>
/// What one provider's ids look like and how Chronicle must treat them. Chronicle knows no provider by name: each plugin
/// declares its own policy (see <c>IExternalIdPolicyProvider</c> in Chronicle.Plugins) and the core only asks
/// Chronicle's ExternalIdPolicies registry. A source with no declared policy gets the plain defaults: ids are
/// stored exactly as issued, they take part in conflict checks, and a cross-reference is written "source:value".
/// </summary>
public sealed class ExternalIdPolicy
{
    /// <summary>The short source key the provider's ids are stored under (the last segment of its plugin id).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// When the id carries the provider's own prefix ("imdb:tt0137523"), other plugins may report it too, and it is
    /// recognised on any raw id from any plugin: it is stored under this source with the prefix removed, so every plugin that
    /// knows the id shares one row per item. Null when the provider's ids are only ever written by the provider itself.
    /// </summary>
    public string? SharedRawIdPrefix { get; set; }

    /// <summary>The declaring plugin's full id (set by Chronicle when the plugin loads, not by the plugin).</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>After <see cref="SharedRawIdPrefix"/> is removed, an id starting with one of these keeps the prefix
    /// (an IMDb person id "nm0000206" is stored "imdb:nm0000206", the form credits record).</summary>
    public string[] KeepSharedPrefixWhenIdStarts { get; set; } = [];

    /// <summary>Entity-type prefixes the provider dispatches on that must be stored in another form: key = prefix as the
    /// provider issues it ("person:"), value = the stored prefix ("tmdb:"). Applied only to ids the provider itself wrote.</summary>
    public Dictionary<string, string> StoredPrefixRewrites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A regular expression naming ids that are unusable and must never be stored (the provider cannot parse them).</summary>
    public string? RefusedIdPattern { get; set; }

    /// <summary>
    /// True when this source's ids are not unique to one item (an artist-level id deliberately shared by many distinct items),
    /// so two items disagreeing on it is no evidence they differ. They are left out of identity-conflict checks.
    /// </summary>
    public bool IgnoredForConflicts { get; set; }

    /// <summary>
    /// True when the id is derived from the item's name (a page title), not an independent identity: two different people
    /// with the same name resolve to the same page, so matching on it alone must never merge them.
    /// </summary>
    public bool NameDerived { get; set; }

    /// <summary>The field of this provider's own metadata partition (e.g. "title") that can arrive carrying a trailing
    /// disambiguation parenthetical ("Heat (film)") which Chronicle should strip. Null when the provider's titles are clean.</summary>
    public string? DisambiguatedTitleField { get; set; }

    /// <summary>
    /// How an id this provider reports inside another provider's cross-reference block is written, per kind of item
    /// ("movie", "tv", "anime", else "default"), with <c>{id}</c> standing for the value. Missing kinds fall back to "default",
    /// and a missing "default" to "{source}:{id}".
    /// </summary>
    public Dictionary<string, string> CrossRefFormats { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The kinds of JSON value that count as an id in a cross-reference block: "number", "string". Both when empty.</summary>
    public string[] CrossRefValueKinds { get; set; } = [];
}
