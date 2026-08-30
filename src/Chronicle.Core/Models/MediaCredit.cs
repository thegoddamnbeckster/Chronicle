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

    /// <summary>
    /// Resolved link to the "people"-type MediaItem this credit belongs to, if resolved --
    /// see PersonResolutionService. Nullable long-term, not just during migration: a credit
    /// whose person can't be confidently resolved still gets stored (PersonName/
    /// ExternalPersonId above preserve provenance) but simply won't appear on any person's
    /// detail page until/unless resolved. This is a derived pointer, not the source of truth
    /// -- PersonName/ExternalPersonId are never replaced by it.
    /// </summary>
    public int? PersonMediaItemId { get; set; }

    // Navigation
    public MediaItem  MediaItem       { get; set; } = null!;
    public MediaItem? PersonMediaItem { get; set; }
}
