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

    /// <summary>
    /// The movie-set (collection) a movie belongs to, sourced from Chronicle's own parent
    /// MediaItem.
    /// <para>
    /// Kodi never scrapes a movie set — it has no getdetails hook for one — so the addon's only
    /// route is writing files into Kodi's "Movie set information folder". That's why every art
    /// type travels here rather than just a poster: whatever isn't in this payload can't reach
    /// Kodi at all.
    /// </para>
    /// </summary>
    public record ScraperCollectionDto(
        int Id,
        string Name,
        string? Overview,
        string? PosterUrl,
        string? BackdropUrl,
        string? LogoUrl = null,
        string? BannerUrl = null,
        string? ClearartUrl = null,
        string? DiscUrl = null,
        string? ThumbUrl = null,
        /// <summary>
        /// Canonical slot names ("poster_url", "disc_url", …) the user has explicitly pinned in
        /// Chronicle. The addon is deliberately fill-only for automatically resolved art so it
        /// never clobbers hand-curated files — but a pin is the user speaking, so those slots
        /// must overwrite instead. Empty when nothing is pinned.
        /// </summary>
        IReadOnlyList<string>? PinnedSlots = null);

    /// <summary>An actor credit -- the performer's name and, when the source provider supplied it,
    /// the character/role they played. Shared between the scraper and web-facing media DTOs.</summary>
    public record CastMemberDto(string Name, string? Role);

    /// <summary>A non-actor credit -- director, writer, producer, executive producer,
    /// composer, etc. Job is null when the source provider only supplies a flat name list.</summary>
    public record CrewMemberDto(string Name, string? Job);

    /// <summary>A season container belonging to a show, as already tracked in Chronicle. PosterUrl
    /// is Chronicle's own top pick (kept for simple callers); Artwork carries every candidate for
    /// every art type this season has, same shape as a movie's or show's own Artwork -- a season
    /// is its own MediaItem with its own per-provider MetadataJson partitions, so it's resolved
    /// exactly the same way.</summary>
    public record ScraperSeasonDto(
        int Id, int Number, string? Name, string? PosterUrl,
        Dictionary<string, List<ScraperArtworkCandidateDto>>? Artwork = null);

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
        List<CastMemberDto>? Cast,
        List<CrewMemberDto>? Crew,
        List<string>? Tags,
        Dictionary<string, ScraperRatingDto>? Ratings,
        string? TrailerUrl,
        ScraperExternalIdsDto? ExternalIds,
        Dictionary<string, List<ScraperArtworkCandidateDto>>? Artwork,
        ScraperCollectionDto? Collection,
        /// <summary>The real video file's own basename (with extension), exactly as Chronicle's
        /// file scanner recorded it -- e.g. "2 Lava 2 Lantula! (2016).mkv". Null when this item
        /// was never scanned from disk by Chronicle (created reactively from a Kodi search
        /// instead). When present, this is a verified fact about the actual file, not a
        /// re-derived title+year guess -- see movie_art_sync.py's find_movie_location(), which
        /// tries an exact match on this filename before falling back to title/year matching.</summary>
        string? KnownFileName
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
        int? RuntimeMinutes,
        List<string>? Genres,
        List<CastMemberDto>? Cast,
        List<CrewMemberDto>? Crew,
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
        string? Aired,
        int? RuntimeMinutes,
        List<CastMemberDto>? Cast,
        List<CrewMemberDto>? Crew,
        Dictionary<string, ScraperRatingDto>? Ratings,
        /// <summary>Chronicle's own top pick for the episode thumb -- kept for simple callers;
        /// equal to Artwork["thumb"][0].Url when Artwork has a thumb candidate at all.</summary>
        string? ThumbUrl,
        ScraperExternalIdsDto? ExternalIds,
        // The parent show's own title/year -- not this episode's -- so the Kodi addon can locate
        // the show's own folder on disk for this episode (see ScraperController.GetEpisodeDetails).
        string? ShowTitle,
        int? ShowYear,
        /// <summary>Every art-type candidate this episode has -- same shape as a movie's own
        /// Artwork. In practice episodes usually only ever have "thumb" candidates (no configured
        /// provider currently supplies per-episode fanart/etc.), but this isn't hardcoded to just
        /// that type -- whatever CollectArtwork actually finds in the episode's own MetadataJson
        /// is passed through, same as every other item type.</summary>
        Dictionary<string, List<ScraperArtworkCandidateDto>>? Artwork = null
    );
}
