namespace Chronicle.Core.Helpers;

/// <summary>
/// File-name extraction that gives the same answer on every operating system.
///
/// <see cref="System.IO.Path.GetFileName(string)"/> only recognises the CURRENT OS's separators: on
/// Windows both '\' and '/', on Linux only '/'. Chronicle stores the file paths its scanners saw
/// (<c>fileScanner.filePaths</c>), and those are often Windows paths ("F:\Videos\Movies\X (2020)\X (2020).mkv")
/// read back on a machine that is not Windows -- the Linux CI runner, or the Docker image. There
/// <c>Path.GetFileName</c> returns the WHOLE string, so a stored path's known file name never matched what
/// Kodi sends and the file was not found. Three tests failed on every CI run for this reason, and every push
/// to main emailed a failure.
/// </summary>
public static class FilePathHelper
{
    /// <summary>
    /// The part of <paramref name="path"/> after its last '\' or '/', whichever OS this runs on. Like
    /// <c>Path.GetFileName</c>: a path ending in a separator gives an empty string, a string with no
    /// separator comes back unchanged, and null/empty gives an empty string.
    /// </summary>
    public static string GetFileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var lastSeparator = path.LastIndexOfAny(['\\', '/']);
        return lastSeparator < 0 ? path : path[(lastSeparator + 1)..];
    }

    private static readonly System.Text.RegularExpressions.Regex BracketedYear =
        new(@"[\(\[]((?:19|20)\d{2})[\)\]]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The "(YYYY)" or "[YYYY]" a file name carries ("Flight Risk (2025).mkv" gives 2025): the last one when there
    /// are several, from the file-name part of a path of either style. Null when the name has none.
    /// </summary>
    public static int? YearInFileName(string? path)
    {
        var matches = BracketedYear.Matches(GetFileName(path));
        return matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, out var year) ? year : null;
    }
}
