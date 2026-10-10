namespace Chronicle.Core.Models;

/// <summary>
/// A file that belongs with a media item without being the item itself: subtitles next to a movie, cover
/// art, theme music, extras, booklets. Recorded by the scanner when related-file bundling is on, so the
/// item knows everything that lives with it. Rows are never deleted by a scan; a file that stops being
/// seen is marked <see cref="MissingSince"/> and cleared again if it reappears.
/// </summary>
public class MediaItemRelatedFile
{
    public int Id { get; set; }
    public int MediaItemId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = RelatedFileKinds.Other;
    public long? SizeBytes { get; set; }
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
    public DateTime? MissingSince { get; set; }

    public MediaItem? MediaItem { get; set; }
}

public static class RelatedFileKinds
{
    public const string Subtitle = "subtitle";
    public const string Artwork = "artwork";
    public const string Theme = "theme";
    public const string Extra = "extra";
    public const string Booklet = "booklet";
    public const string Other = "other";
}
