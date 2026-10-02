using System.Text.RegularExpressions;

namespace Chronicle.Core.Helpers;

public static class SimklIdHelper
{
    private static readonly Regex _untyped = new(@"^simkl:\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// True for the legacy "simkl:NNN" form with no type segment. The canonical form is
    /// "simkl:{movie|tv|anime}:NNN" (Chronicle.Plugin.Simkl, CrossRefHelper). The plugin can't
    /// parse the untyped form -- it throws, falls back to a text search, and wastes an API
    /// call -- and the same Simkl id stored in two shapes defeats exact-string id matching.
    /// Found 1,380 of them on 2026-10-02, left by an old cross-ref pass; all were normalized.
    /// An episode id ("simkl:123:s1e2") is a different thing and is not untyped.
    /// </summary>
    public static bool IsUntyped(string? externalId) =>
        externalId is not null && _untyped.IsMatch(externalId);
}
