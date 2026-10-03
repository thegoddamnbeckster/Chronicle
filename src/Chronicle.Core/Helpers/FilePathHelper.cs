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
}
