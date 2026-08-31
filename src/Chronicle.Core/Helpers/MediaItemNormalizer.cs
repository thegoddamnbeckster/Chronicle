using System.Text.RegularExpressions;

namespace Chronicle.Core.Helpers;

public static class MediaItemNormalizer
{
    private static readonly Regex _strip =
        new(@"[.\-,':!?()]", RegexOptions.Compiled);
    private static readonly Regex _spaces =
        new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Produces a canonical lowercase string for duplicate detection.
    /// Strips common punctuation to nothing, collapses whitespace, trims.
    /// "James S. A. Corey" → "james s a corey"
    /// "James S.A. Corey"  → "james sa corey"
    /// "James S.A.Corey"   → "james sacorey"
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var stripped = _strip.Replace(name, string.Empty);
        var collapsed = _spaces.Replace(stripped, " ").Trim().ToLowerInvariant();
        return collapsed;
    }

    /// <summary>
    /// A stricter variant of <see cref="NormalizeName"/> for exactly the case its own doc
    /// comment already flags as unresolved: "James S. A. Corey" and "James S.A. Corey" strip
    /// down to different strings ("james s a corey" vs "james sa corey") because whether a
    /// space happened to sit next to the punctuation survives the strip. Root-caused a real
    /// duplicate (2026-08-31, Hardcover audiobook authors): two MediaItems for the same person,
    /// spaced differently around their initials, matched by NormalizeName as different. This
    /// removes ALL whitespace (not just collapsing runs to one space) so spacing around
    /// initials/abbreviations can no longer be the sole difference between two names. Kept
    /// separate from NormalizeName -- which many existing duplicate-detection call sites
    /// already depend on for its current, less aggressive behavior -- rather than changing it
    /// in place; use this as an additional fallback comparison tier, not a replacement.
    /// </summary>
    public static string NormalizeNameLoose(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var stripped = _strip.Replace(name, string.Empty);
        return _spaces.Replace(stripped, string.Empty).ToLowerInvariant();
    }
}
