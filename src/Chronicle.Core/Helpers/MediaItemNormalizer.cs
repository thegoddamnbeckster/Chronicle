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
}
