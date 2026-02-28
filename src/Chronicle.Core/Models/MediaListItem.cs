namespace Chronicle.Core.Models;

public class MediaListItem
{
    public int Id { get; set; }
    public int ListId { get; set; }
    public int MediaItemId { get; set; }

    /// <summary>
    /// Zero-based position used when the parent list is ordered.
    /// Ignored (but preserved) for unordered lists.
    /// </summary>
    public int Position { get; set; } = 0;

    public string? Notes { get; set; }
    public DateTime AddedAt { get; set; }

    // Navigation
    public MediaList? List { get; set; }
    public MediaItem? MediaItem { get; set; }
}
