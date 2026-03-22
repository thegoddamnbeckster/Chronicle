using System.Text.RegularExpressions;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Services.Scan;

/// <summary>
/// Built-in implementation of <see cref="IFileScannerPlugin"/> that ships with Chronicle.
/// Uses local file system enumeration combined with <see cref="FolderSignalExtractor"/>,
/// <see cref="TagSignalExtractor"/>, and <see cref="NfoSignalExtractor"/> to produce
/// rich <see cref="ScannedFile"/> results without any external API calls.
/// </summary>
public sealed class BuiltInFileScannerPlugin : IFileScannerPlugin
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string PluginId    => "chronicle.plugin.filescanner.builtin";
    public string Name        => "Built-in File Scanner";
    public string Version     => "1.0.0";
    public string Author      => "Chronicle";
    public string Description => "Scans local directories and extracts metadata from filenames, NFO sidecars, and embedded audio/video tags.";

    // ── Internal state ────────────────────────────────────────────────────────

    /// <summary>Per-media-type thresholds populated by <see cref="Configure"/>.</summary>
    private readonly Dictionary<string, int> _thresholds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Legacy single-threshold fallback (populated if old key is still present).</summary>
    private int? _legacyThreshold;

    private static readonly FolderSignalExtractor _folderExtractor = new();
    private static readonly TagSignalExtractor    _tagExtractor    = new();
    private static readonly NfoSignalExtractor    _nfoExtractor    = new();

    // Filename patterns ────────────────────────────────────────────────────────

    // "Title (Year)" or "Title.Year." or "Title_Year_"
    private static readonly Regex _titleYearClean =
        new(@"^(.+?)\s*[\(\[\{](\d{4})[\)\]\}]", RegexOptions.Compiled);

    private static readonly Regex _titleYearDotted =
        new(@"^(.+?)[\._](\d{4})[\._]", RegexOptions.Compiled);

    // SxxExx episode code
    private static readonly Regex _episodeCode =
        new(@"[Ss](\d{1,2})[Ee](\d{1,3})", RegexOptions.Compiled);

    // Recognised media file extensions
    private static readonly HashSet<string> _mediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".m2ts",
        ".mpg", ".mpeg", ".flv", ".webm", ".vob", ".divx",
        // Audio
        ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wma", ".aac",
        ".wav", ".aiff", ".ape", ".mpc",
    };

    // Poster image file names to look for alongside media files
    private static readonly string[] _posterFileNames =
        ["poster.jpg", "poster.png", "folder.jpg", "folder.png", "cover.jpg", "cover.png"];

    // ── Capability declarations ───────────────────────────────────────────────

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new() { MediaTypeName = "movies", DefaultPriority = 10 },
        new() { MediaTypeName = "tv",     DefaultPriority = 10 },
        new() { MediaTypeName = "music",  DefaultPriority = 10 },
    ];

    /// <summary>
    /// Generates one threshold setting per supported media type so each can be tuned
    /// independently. The schema is driven by <see cref="GetSupportedMediaTypes"/> so any
    /// future media types added there automatically appear in the settings UI.
    /// Note: enrichment after import only runs for media types that also have a compatible
    /// metadata plugin installed (e.g. TMDB for movies/TV, MusicBrainz for music).
    /// </summary>
    public PluginSettingsSchema GetSettingsSchema()
    {
        return new PluginSettingsSchema
        {
            Settings = GetSupportedMediaTypes()
                .Select(mt => new SettingDefinition
                {
                    Key          = $"confidence_threshold_{mt.MediaTypeName}",
                    Label        = $"Confidence threshold — {FriendlyName(mt.MediaTypeName)} (0–100)",
                    Description  = ConfidenceDescription(mt.MediaTypeName),
                    Type         = SettingType.Number,
                    Required     = false,
                    DefaultValue = "75",
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Returns a per-media-type description of how confidence scores are computed,
    /// so users understand exactly what to expect when adjusting the threshold.
    /// </summary>
    private static string ConfidenceDescription(string mediaTypeName)
    {
        const string header =
            "Minimum confidence score (0–100) for a group to be auto-imported by the " +
            "scheduled scan. Groups below this score appear on the manual Scan page but " +
            "are skipped by background tasks.\n\n";

        return mediaTypeName switch
        {
            "movies" =>
                header +
                "How scores are assigned for Movies:\n" +
                "• 100 — NFO sidecar has an external ID (e.g. tmdbid tag)\n" +
                "• 90  — NFO sidecar has title + year\n" +
                "• 78  — NFO sidecar has title only\n" +
                "• 75  — Folder name includes a year, e.g. \"Interstellar (2014)\"\n" +
                "• 55  — Folder name only — no year, no sidecar\n\n" +
                "Recommended: 75 for year-named folders; lower to 55 to import everything; " +
                "raise to 90+ to require NFO sidecars.",

            "tv" =>
                header +
                "How scores are assigned for TV Shows (score is for the show root folder):\n" +
                "• Base 55  — Folder name alone, e.g. \"Breaking Bad\"\n" +
                "• +20      — Folder name includes a year, e.g. \"Breaking Bad (2008)\"\n" +
                "• +20      — NFO sidecar in show folder has a show title\n" +
                "• −15      — Audio tag artist name conflicts with folder name\n\n" +
                "Typical results: folder+year = 75, folder+NFO = 75, folder+year+NFO = 95, " +
                "folder only = 55.\n\n" +
                "Recommended: 75 for year-named show folders; 55 to import everything.",

            "music" =>
                header +
                "How scores are assigned for Music (score is for the artist root folder):\n" +
                "• Base 55  — Folder name alone, e.g. \"Metallica\"\n" +
                "• +20      — Embedded audio tags have an artist name\n" +
                "• +20      — NFO sidecar has an artist name\n" +
                "• +20      — Folder name includes a year, e.g. \"Metallica (1981)\"\n" +
                "• −15      — Tag artist name conflicts with folder name\n\n" +
                "Typical results: folder+tags = 75, folder+NFO = 75, folder+tags+year = 95, " +
                "folder only = 55.\n\n" +
                "Recommended: 75 requires at least one corroborating signal; 55 imports everything.",

            _ =>
                header +
                "How scores are assigned:\n" +
                "• 100 — NFO sidecar has an external ID\n" +
                "• 90  — NFO sidecar has title + year\n" +
                "• 78  — NFO sidecar has title only\n" +
                "• 75  — Folder name includes a year\n" +
                "• 55  — Folder name only\n\n" +
                "Recommended: 75 for year-named folders; 55 to import everything.",
        };
    }

    private static string FriendlyName(string mediaTypeName) => mediaTypeName switch
    {
        "movies" => "Movies",
        "tv"     => "TV Shows",
        "music"  => "Music",
        _        => System.Globalization.CultureInfo.CurrentCulture.TextInfo
                        .ToTitleCase(mediaTypeName),
    };

    // ConfidenceThreshold returns the fallback default; per-type values are in _thresholds.
    public int ConfidenceThreshold => 75;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        // Per-media-type thresholds (new schema)
        foreach (var mt in GetSupportedMediaTypes())
        {
            var key = $"confidence_threshold_{mt.MediaTypeName}";
            if (settings.TryGetValue(key, out var raw)
                && int.TryParse(raw, out var parsed)
                && parsed >= 0 && parsed <= 100)
            {
                _thresholds[mt.MediaTypeName] = parsed;
            }
        }

        // Legacy fallback: single "confidence_threshold" key
        if (settings.TryGetValue("confidence_threshold", out var legacyRaw)
            && int.TryParse(legacyRaw, out var legacyParsed)
            && legacyParsed >= 0 && legacyParsed <= 100)
        {
            _legacyThreshold = legacyParsed;
        }
    }

    public int GetConfidenceThreshold(string mediaTypeName)
    {
        if (_thresholds.TryGetValue(mediaTypeName, out var threshold))
            return threshold;
        return _legacyThreshold ?? ConfidenceThreshold;
    }

    // ── Core operation ────────────────────────────────────────────────────────

    public Task<List<ScannedFile>> ScanDirectoryAsync(
        string path,
        bool recursive,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Scan path does not exist: {path}");

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var results = new List<ScannedFile>();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(path, "*", searchOption);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to enumerate files in '{path}': {ex.Message}", ex);
        }

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(filePath);
            if (!_mediaExtensions.Contains(ext))
                continue;

            var scanned = ParseFile(filePath, path);
            results.Add(scanned);
        }

        return Task.FromResult(results);
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) =>
        Task.FromResult(true);

    // ── Per-file parsing ──────────────────────────────────────────────────────

    private static ScannedFile ParseFile(string filePath, string scanRoot)
    {
        var scanned = new ScannedFile
        {
            FilePath      = filePath,
            FileSizeBytes = GetFileSizeBytes(filePath),
        };

        // 1. Folder signal — structure, episode/track numbers
        var folderSig = _folderExtractor.Extract(filePath, scanRoot);

        // 2. NFO sidecar — highest quality metadata
        var nfoPath = _nfoExtractor.FindSidecar(filePath);
        NfoSignal? nfoSig = nfoPath is not null ? _nfoExtractor.Extract(nfoPath) : null;

        // 3. Embedded tags (audio / container)
        var tagSig = _tagExtractor.Extract(filePath);

        // 4. Filename-based parsing as fallback
        var fileName = folderSig.FileName; // no extension
        var (fnTitle, fnYear) = ParseTitleYear(fileName);

        // ── Populate ScannedFile from signals (priority: NFO > tag > filename) ────

        // Title
        scanned.ParsedTitle =
            nfoSig?.Title
            ?? tagSig?.Title
            ?? fnTitle;

        // Year
        scanned.ParsedYear =
            nfoSig?.Year
            ?? (tagSig?.Year.HasValue == true ? (int?)tagSig.Year.Value : null)
            ?? fnYear;

        // External ID from NFO
        scanned.SuggestedExternalId = nfoSig?.ExternalId;

        // Poster URLs
        scanned.NfoPosterUrl    = nfoSig?.PosterUrl;
        scanned.LocalPosterPath = FindLocalPoster(filePath);

        // TV fields
        scanned.ShowTitle     = nfoSig?.ShowTitle;
        scanned.SeasonNumber  = nfoSig?.Season  ?? folderSig.DetectedSeason;
        scanned.EpisodeNumber = nfoSig?.Episode ?? folderSig.DetectedEpisode;

        // Detect episode title from filename after SxxExx code
        if (scanned.EpisodeNumber.HasValue)
        {
            var epMatch = _episodeCode.Match(fileName);
            if (epMatch.Success)
            {
                var afterCode = fileName[(epMatch.Index + epMatch.Length)..].Trim(' ', '-', '_', '.');
                if (!string.IsNullOrWhiteSpace(afterCode))
                    scanned.EpisodeTitle = NormaliseTitle(afterCode);
            }
        }

        // Audio tag fields
        if (tagSig is not null)
        {
            scanned.AudioArtist      = tagSig.Artist;
            scanned.AudioAlbumArtist = tagSig.AlbumArtist;
            scanned.AudioAlbum       = tagSig.Album;
            scanned.AudioTrackNumber = tagSig.TrackNumber.HasValue ? (int?)tagSig.TrackNumber.Value : null;
            scanned.AudioDiscNumber  = tagSig.DiscNumber.HasValue  ? (int?)tagSig.DiscNumber.Value  : null;
            scanned.AudioYear        = tagSig.Year.HasValue        ? (int?)tagSig.Year.Value        : null;
            scanned.AudioGenre       = tagSig.Genre;
        }

        // Folder-based track/disc numbers (fallback when tags are absent)
        scanned.AudioTrackNumber ??= folderSig.DetectedTrackNumber;
        scanned.AudioDiscNumber  ??= folderSig.DetectedDiscNumber;

        // Media type hint
        scanned.MediaTypeHint = InferMediaTypeHint(scanned, folderSig);

        // Confidence score
        scanned.ConfidenceScore = ComputeConfidence(scanned, nfoSig, tagSig);

        return scanned;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Parses title and optional year from a bare filename (no extension).</summary>
    private static (string title, int? year) ParseTitleYear(string fileName)
    {
        var m = _titleYearClean.Match(fileName);
        if (m.Success)
            return (NormaliseTitle(m.Groups[1].Value), int.Parse(m.Groups[2].Value));

        var md = _titleYearDotted.Match(fileName);
        if (md.Success)
            return (NormaliseTitle(md.Groups[1].Value), int.Parse(md.Groups[2].Value));

        return (NormaliseTitle(fileName), null);
    }

    /// <summary>
    /// Replaces dots/underscores used as word separators with spaces, trims trailing junk.
    /// </summary>
    private static string NormaliseTitle(string raw)
    {
        // Strip common release-group/quality suffixes first
        var clean = Regex.Replace(raw,
            @"[\._\-](BluRay|Blu-Ray|WEB-DL|WEBRip|HDRip|DVDRip|BDRip|HDTV|720p|1080p|2160p|4K|x264|x265|HEVC|YIFY|EXTENDED|REMASTERED|PROPER|REPACK|DC|THEATRICAL).*$",
            string.Empty, RegexOptions.IgnoreCase).Trim();

        // Replace dots/underscores that look like word separators
        clean = Regex.Replace(clean, @"[\._]+", " ").Trim();

        return clean;
    }

    private static string InferMediaTypeHint(ScannedFile f, FolderSignal folder)
    {
        // Audio-tagged file → music
        if (f.AudioArtist != null || f.AudioAlbum != null)
            return "music";

        // Has episode number → TV
        if (f.EpisodeNumber.HasValue || f.ShowTitle != null || folder.DetectedSeason.HasValue)
            return "tv";

        var ext = Path.GetExtension(f.FilePath).ToLowerInvariant();
        if (ext is ".mp3" or ".flac" or ".m4a" or ".ogg" or ".opus" or ".wma" or ".aac"
                or ".wav" or ".aiff" or ".ape" or ".mpc")
            return "music";

        return "movies";
    }

    /// <summary>
    /// Assigns a confidence score reflecting how much structural signal was found.
    /// Mirrors the scoring levels documented in <see cref="ScannedFile.ConfidenceScore"/>.
    /// </summary>
    private static int ComputeConfidence(ScannedFile f, NfoSignal? nfo, TagSignal? tag)
    {
        if (nfo != null)
        {
            if (nfo.ExternalId != null)                  return 100;
            if (nfo.Title != null && nfo.Year.HasValue)  return 85;
            if (nfo.Title != null)                       return 78;
        }

        if (tag != null)
        {
            if (tag.Title != null && tag.Year.HasValue)  return 82;
            if (tag.Title != null)                       return 70;
            if (tag.Album != null)                       return 65;
        }

        if (f.ParsedYear.HasValue)                       return 75;
        if (!string.IsNullOrWhiteSpace(f.ParsedTitle))   return 45;

        return 20;
    }

    private static string? FindLocalPoster(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is null) return null;

        foreach (var name in _posterFileNames)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static long? GetFileSizeBytes(string filePath)
    {
        try { return new FileInfo(filePath).Length; }
        catch { return null; }
    }
}
