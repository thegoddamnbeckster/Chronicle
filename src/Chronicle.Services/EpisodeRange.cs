using System.Text.RegularExpressions;

namespace Chronicle.Services;

/// <summary>Which episodes a file name says it holds: "S01E01-E02", "S01E01E02", "S01E01-02" and "S01E01-E03" (1 to 3).</summary>
public static class EpisodeRange
{
    private static readonly Regex Multi = new(@"[Ss](\d{1,2})[Ee](\d{1,3})((?:\s*(?:-|[Ee]|-\s*[Ee])\s*\d{1,3})+)", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"\d{1,3}", RegexOptions.Compiled);

    /// <summary>The first and last episode the name covers, or null when it names a single episode (or none).</summary>
    public static (int First, int Last)? Of(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var m = Multi.Match(fileName);
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out var first)) return null;
        var last = first;
        foreach (Match n in Number.Matches(m.Groups[3].Value))
            if (int.TryParse(n.Value, out var v) && v > last) last = v;
        // A range longer than a handful of episodes is a mistake in the name, not a real multi-episode file.
        return last > first && last - first <= 5 ? (first, last) : null;
    }

    public static bool Covers(string? fileName, int episode) =>
        Of(fileName) is { } r && episode >= r.First && episode <= r.Last;
}
