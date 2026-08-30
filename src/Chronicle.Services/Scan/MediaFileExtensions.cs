namespace Chronicle.Services.Scan;

/// <summary>
/// The single source of truth for "is this file extension an actual playable media file" --
/// shared by <see cref="BuiltInFileScannerPlugin"/> (which recognized files during a real scan)
/// and <see cref="ScanGroupingService"/> (which must apply the exact same rule when deciding
/// whether an arbitrary file on disk belongs in a group's importable Files list). Before this
/// was shared, ScanGroupingService used a denylist instead ("not a known sidecar extension" =
/// "must be a media file"), which silently imported junk like Kodi's own ".metathumb" cache
/// files as if they were the movie itself (confirmed 2026-08-29).
/// </summary>
internal static class MediaFileExtensions
{
    public static readonly HashSet<string> Recognized = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".m2ts",
        ".mpg", ".mpeg", ".flv", ".webm", ".vob", ".divx", ".3gp",
        // Audio
        ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wma", ".aac",
        ".wav", ".aiff", ".ape", ".mpc", ".wv",
    };
}
