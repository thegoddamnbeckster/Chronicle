namespace Chronicle.Core.Models;

public class MediaCredit
{
    public int     Id               { get; set; }
    public int     MediaItemId      { get; set; }
    public string  PersonName       { get; set; } = string.Empty;
    public string  Role             { get; set; } = string.Empty;  // "Director" | "Actor" | …
    public string? CharacterName    { get; set; }
    public int?    BillingOrder     { get; set; }
    public string  Source           { get; set; } = string.Empty;  // "trakt" | "tmdb" | …
    public string? ExternalPersonId { get; set; }
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;

    // Navigation
    public MediaItem MediaItem { get; set; } = null!;
}
