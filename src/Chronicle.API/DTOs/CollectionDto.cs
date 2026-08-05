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
}
