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

    /// <summary>Absolute path to a local poster/folder image found alongside the media file.</summary>
    public string? LocalPosterPath { get; set; }

    /// <summary>
    /// Hint for which media type this file likely belongs to.
    /// Typically "movies" or "tv"; matches the <c>Name</c> column in the media_types table.
    /// </summary>
    public string MediaTypeHint { get; set; } = "movies";
}
