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

/// <summary>
/// The extensions the scanner treats as media for one scan: Chronicle's built-in list plus any the administrator added
/// in <c>scan.extra_video_extensions</c> / <c>scan.extra_audio_extensions</c> (comma separated; a leading dot is optional).
/// </summary>
internal sealed class MediaFileRules
{
    public const string ExtraVideoKey = "scan.extra_video_extensions";
    public const string ExtraAudioKey = "scan.extra_audio_extensions";

    public HashSet<string> Recognized { get; }
    public HashSet<string> Audio { get; }

    private MediaFileRules(HashSet<string> recognized, HashSet<string> audio)
    {
        Recognized = recognized;
        Audio = audio;
    }

    public static MediaFileRules Defaults { get; } = From(null);

    public static MediaFileRules From(IReadOnlyDictionary<string, string>? settings)
    {
        var recognized = new HashSet<string>(MediaFileExtensions.Recognized, StringComparer.OrdinalIgnoreCase);
        var audio = new HashSet<string>(TrackFileName.AudioExtensions, StringComparer.OrdinalIgnoreCase);

        foreach (var ext in Read(settings, ExtraVideoKey)) recognized.Add(ext);
        foreach (var ext in Read(settings, ExtraAudioKey)) { recognized.Add(ext); audio.Add(ext); }
        return new MediaFileRules(recognized, audio);
    }

    private static IEnumerable<string> Read(IReadOnlyDictionary<string, string>? settings, string key)
    {
        if (settings is null || !settings.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var ext = part.StartsWith('.') ? part : "." + part;
            // Only a plain extension: letters and digits, so a typo can never make a path-like entry.
            if (ext.Length is > 1 and <= 12 && ext.Skip(1).All(char.IsLetterOrDigit)) yield return ext.ToLowerInvariant();
        }
    }
}
