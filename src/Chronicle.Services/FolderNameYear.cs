using System.Text.RegularExpressions;

namespace Chronicle.Services;

/// <summary>
/// Splits a release year off a folder-style name: "(2000) The Better Life" and "The Better Life (2000)" both
/// become the name "The Better Life" and the year 2000. The year belongs in the item's Year field, never in its
/// name -- a name carrying it can't match what any metadata or listening service calls the album.
/// </summary>
public static class FolderNameYear
{
    private static readonly Regex Leading =
        new(@"^\s*[\(\[]\s*(\d{4})\s*[\)\]]\s*[-–]?\s*(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex Trailing =
        new(@"^(?<rest>.*?)\s*[\(\[]\s*(\d{4})\s*[\)\]]\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The name without its bracketed year, and that year when it is a plausible release year. A name with no
    /// bracketed year, an implausible one, or nothing left once it is removed comes back unchanged with no year.
    /// </summary>
    public static (string Name, int? Year) Split(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (name ?? string.Empty, null);
        foreach (var re in new[] { Leading, Trailing })
        {
            var m = re.Match(name);
            if (!m.Success) continue;
            var rest = m.Groups["rest"].Value.Trim();
            if (rest.Length == 0 || !int.TryParse(m.Groups[1].Value, out var year) || !ReleaseYear.IsPlausible(year))
                continue;
            return (rest, year);
        }
        return (name, null);
    }
}
