namespace Chronicle.API.DTOs;

public class RebuildCollectionResultDto
{
    public string Summary { get; set; } = string.Empty;
    /// <summary>Updated collection data, or null if the collection was removed entirely.</summary>
    public CollectionDto? Collection { get; set; }
}

public class CollectionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public string? Overview { get; set; }
    public List<CollectionMemberDto> Movies { get; set; } = [];
    /// <summary>
    /// True for movie-like collections (movies, fanedits, anime) whose membership is
    /// automatically maintained from a plugin's TMDB-style "belongs to collection" metadata.
    /// False for manually-created collections of any other media type, where there's no
    /// external source to rebuild against — the frontend uses this to hide the Rebuild
    /// Collection button rather than offering an action that would silently no-op.
    /// </summary>
    public bool SupportsRebuild { get; set; }
}

public class CollectionMemberDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? PosterUrl { get; set; }
    public bool InLibrary { get; set; }
    public string? LibraryStatus { get; set; }
    public double? Rating { get; set; }
    public int? UserRating { get; set; }
    public string? UserRatingSource { get; set; }
    public bool IsStub { get; set; }
    /// <summary>
    /// True if Chronicle has any record of a real local file for this item -- its own file
    /// scanner ("fileScanner.filePaths") or a Kodi scraper's own filesystem discovery reported
    /// back after the fact ("scraperResolvedFile.fileName"); see
    /// <see cref="Chronicle.Services.Scan.FileIdentityJson.HasKnownFile"/>. Deliberately separate
    /// from <see cref="IsStub"/> and <see cref="InLibrary"/> -- neither means "you have the file":
    /// IsStub is false for a real, fully-identified MediaItem imported purely from watch-history
    /// sync (SIMKL/Trakt), and InLibrary only means a UserLibrary row exists, which watch-history
    /// import creates too (often with Status=Completed, since the sync is recording that the user
    /// really did watch it -- just not necessarily through a file Chronicle has). Confirmed live
    /// 2026-08-20: "Behind the Mask: The Rise of Leslie Vernon" and "Sinister Circle" both showed
    /// as if owned on their collection pages (no stub badge) purely because a SIMKL watch-history
    /// entry gave them IsStub=false and a Completed UserLibrary row, despite Chronicle never
    /// having scanned a physical file for either. The frontend badge needs this, not IsStub, to
    /// answer "do I actually have this file" rather than "is this a placeholder row."
    /// </summary>
    public bool HasFile { get; set; }
}
