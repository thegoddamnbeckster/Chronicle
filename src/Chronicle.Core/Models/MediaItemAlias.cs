namespace Chronicle.Core.Models;

public class MediaItemAlias
{
    public int Id { get; set; }
    public int MediaItemId { get; set; }
    public string Alias { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // "merge", "plugin:hardcover", etc.
    public DateTime CreatedAt { get; set; }

    public MediaItem? MediaItem { get; set; }
}
