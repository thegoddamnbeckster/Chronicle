namespace Chronicle.Plugins.Models;

/// <summary>
/// Represents a single media file discovered during a local file system scan.
/// Produced by <see cref="IFileScannerPlugin.ScanDirectoryAsync"/>.
/// </summary>
public class ScannedFile
{
    /// <summary>Absolute path to the media file on the server's file system.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Title parsed from the filename or NFO sidecar.</summary>
    public string ParsedTitle { get; set; } = string.Empty;

    /// <summary>Release year, if determinable from the filename or NFO.</summary>
    public int? ParsedYear { get; set; }

    /// <summary>
    /// Confidence score from 0–100 indicating how reliable the parse is.
    /// 100 = NFO with external ID; 85 = NFO title+year; 75 = clean "Title (Year)";
    /// 65 = dotted pattern; 45 = title only.
    /// </summary>
    public int ConfidenceScore { get; set; }

    /// <summary>
    /// External ID in Chronicle format (e.g. "movie:550") extracted from an NFO file.
    /// Null when no NFO is present or NFO has no recognised external ID field.
    /// </summary>
    public string? SuggestedExternalId { get; set; }

    /// <summary>Poster/thumb URL from the NFO's &lt;thumb&gt; element, if present.</summary>
    public string? NfoPosterUrl { get; set; }

    /// <summary>
    /// Absolute path to the .nfo sidecar file, if one was found alongside the media file.
    /// Only the handful of matching-relevant fields (title/year/season/episode/external id/
    /// poster) are parsed at scan time — this path lets the richer fields (plot, cast, genres,
    /// rating, etc.) be parsed on demand for display without re-scanning.
    /// </summary>
    public string? NfoPath { get; set; }

    /// <summary>Absolute path to a local poster/folder image found alongside the media file.</summary>
    public string? LocalPosterPath { get; set; }

    /// <summary>
    /// Hint for which media type this file likely belongs to.
    /// Typically "movies" or "tv"; matches the <c>Name</c> column in the media_types table.
    /// </summary>
    public string MediaTypeHint { get; set; } = "movies";

    // ── TV / Episode hierarchy ──────────────────────────────────────────────
    /// <summary>Show name parsed from filename before the SxxExx code.</summary>
    public string? ShowTitle { get; set; }
    /// <summary>Season number parsed from SxxExx / NxNN pattern.</summary>
    public int? SeasonNumber { get; set; }
    /// <summary>Episode number parsed from SxxExx / NxNN pattern.</summary>
    public int? EpisodeNumber { get; set; }
    /// <summary>Episode title — text after the SxxExx code, if present in filename.</summary>
    public string? EpisodeTitle { get; set; }

    // ── Music / Audio tags ──────────────────────────────────────────────────
    /// <summary>Primary performer read from the file's embedded audio tags (e.g. ID3 TPE1).</summary>
    public string? AudioArtist { get; set; }
    /// <summary>Album artist read from embedded tags (e.g. ID3 TPE2 / FLAC ALBUMARTIST).</summary>
    public string? AudioAlbumArtist { get; set; }
    /// <summary>Album title read from embedded audio tags.</summary>
    public string? AudioAlbum { get; set; }
    /// <summary>Track number within the album, read from embedded audio tags. Null when absent or zero.</summary>
    public int? AudioTrackNumber { get; set; }
    /// <summary>Disc number within a multi-disc release, read from embedded audio tags. Null when absent or zero.</summary>
    public int? AudioDiscNumber { get; set; }
    /// <summary>Release year read from embedded audio tags. Null when absent or zero.</summary>
    public int? AudioYear { get; set; }
    /// <summary>Genre string read from embedded audio tags.</summary>
    public string? AudioGenre { get; set; }
    /// <summary>
    /// Grouping / series name read from embedded tags (iTunes ©grp / ID3 TIT1).
    /// Audiobook managers store the series name here (e.g. "The Kingkiller Chronicle").
    /// </summary>
    public string? AudioGrouping { get; set; }

    // ── Container / embedded video tags ────────────────────────────────────
    /// <summary>Title embedded in the media container's tag (distinct from ParsedTitle, which is filename-derived).</summary>
    public string? ContainerTitle { get; set; }
    /// <summary>Release year embedded in the media container's tag.</summary>
    public int? ContainerYear { get; set; }
    /// <summary>Description or comment string embedded in the media container's tag.</summary>
    public string? ContainerDescription { get; set; }

    // ── Technical ───────────────────────────────────────────────────────────
    /// <summary>Duration of the media file in whole seconds, as reported by the container. Null for formats TagLib# cannot probe.</summary>
    public int? DurationSeconds { get; set; }
    /// <summary>
    /// Total duration in seconds across all files that were collapsed into this representative entry.
    /// Set by <c>CollapseAudiobooksToFolders</c> when merging a multi-file audiobook.
    /// For single-file items this equals <see cref="DurationSeconds"/>.
    /// </summary>
    public int? TotalDurationSeconds { get; set; }
    /// <summary>Size of the media file in bytes at the time of scanning.</summary>
    public long? FileSizeBytes { get; set; }
}
