namespace Chronicle.API.DTOs
{
    /// <summary>
    /// Cross-provider identifiers pulled from whichever plugin partitions in a
    /// MediaItem's MetadataJson happen to carry them (e.g. Trakt's "ids" block,
    /// TVmaze's flat "imdb"/"tvdb" fields). Any field may be null if no configured
    /// provider currently supplies it for this item.
    /// </summary>
    public record ScraperExternalIdsDto(string? Imdb, string? Tvdb, string? Tmdb, string? Trakt);

    /// <summary>One provider's rating for an item, keyed by that provider's own "source" name.</summary>
    public record ScraperRatingDto(double Rating, int? Votes);

    /// <summary>One candidate image for a given Kodi art type, tagged with the provider it came from.</summary>
    public record ScraperArtworkCandidateDto(string Url, string Source);

    /// <summary>The movie-set (collection) a movie belongs to, sourced from Chronicle's own parent MediaItem.</summary>
    public record ScraperCollectionDto(int Id, string Name, string? Overview, string? PosterUrl, string? BackdropUrl);

    /// <summary>A season container belonging to a show, as already tracked in Chronicle.</summary>
    public record ScraperSeasonDto(int Id, int Number, string? Name, string? PosterUrl);

    public record ScraperMovieDetailsDto(
        string? Title,
        string? Overview,
        string? Tagline,
        int? Year,
        string? Premiered,
        string? Mpaa,
        string? Country,
        string? Studio,
        int? RuntimeMinutes,
        List<string>? Genres,
        List<string>? Cast,
        List<string>? Directors,
        List<string>? Tags,
        Dictionary<string, ScraperRatingDto>? Ratings,
        string? TrailerUrl,
        ScraperExternalIdsDto? ExternalIds,
        Dictionary<string, List<ScraperArtworkCandidateDto>>? Artwork,
        ScraperCollectionDto? Collection
    );

    public record ScraperShowDetailsDto(
        string? Title,
        string? Overview,
        string? Tagline,
        int? Year,
        string? Premiered,
        string? Mpaa,
        string? Country,
        string? Studio,
        string? Status,
        List<string>? Genres,
        List<string>? Cast,
        List<string>? Tags,
        Dictionary<string, ScraperRatingDto>? Ratings,
        string? TrailerUrl,
        ScraperExternalIdsDto? ExternalIds,
        Dictionary<string, List<ScraperArtworkCandidateDto>>? Artwork,
        List<ScraperSeasonDto>? Seasons
    );

    public record ScraperEpisodeSummaryDto(int Id, int Season, int Episode, string? Title);

    public record ScraperEpisodeDetailsDto(
        string? Title,
        string? Overview,
        int Season,
        int Episode,
        int? Year,
        List<string>? Cast,
        List<string>? Directors,
        Dictionary<string, ScraperRatingDto>? Ratings,
        string? ThumbUrl,
        ScraperExternalIdsDto? ExternalIds
    );
}
