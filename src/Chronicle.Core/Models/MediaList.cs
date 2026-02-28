namespace Chronicle.Core.Models;

public class MediaList
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// When true, items are stored in a user-defined order (e.g. the MCU Infinity Saga).
    /// When false, the list is an unordered collection (e.g. "Films to watch with Mum").
    /// </summary>
    public bool IsOrdered { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public User? User { get; set; }
    public ICollection<MediaListItem> Items { get; set; } = new List<MediaListItem>();
}
