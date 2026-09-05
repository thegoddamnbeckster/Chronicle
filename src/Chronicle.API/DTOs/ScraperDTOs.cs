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
    /// <summary>ThumbUrl is the credited person's own resolved headshot (Chronicle's
    /// person_headshots-backed poster, see docs/plans/2026-08-28-people-section-design.md
    /// Section 7) -- null when Chronicle hasn't resolved a photo for them yet. Kodi's own
    /// actor NFO schema already supports &lt;actor&gt;&lt;thumb&gt;, previously just never
    /// supplied.</summary>
    public record CastMemberDto(string Name, string? Role, string? ThumbUrl = null);

    /// <summary>A non-actor credit -- director, writer, producer, executive producer,
    /// composer, etc. Job is null when the source provider only supplies a flat name list.</summary>
    public record CrewMemberDto(string Name, string? Job);

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
        string? KnownFileName,
        /// <summary>The calling user's own 1-10 rating for this item in Chronicle, if any --
        /// always pushed to Kodi as-is (see progress_sync.py's module doc: Kodi has no
        /// per-rating timestamp, so there's no safe "which side is newer" comparison to make
        /// here, unlike ResumePositionPercent below).</summary>
        int? UserRating = null,
        /// <summary>The calling user's resume position for this item in Chronicle, 0-100.
        /// Genuinely bidirectional: the scraper compares this against Kodi's own local
        /// lastplayed/resume and reconciles in whichever direction is more recent.</summary>
        double? ResumePositionPercent = null,
        DateTime? ResumeUpdatedAt = null,
        /// <summary>True when the calling user's UserLibrary row for this item is Completed.
        /// Deliberately separate from ResumePositionPercent/ResumeUpdatedAt above -- those are
        /// cleared to null the moment an item is marked watched (nothing left to "resume"),
        /// which meant a fully-completed watch carried no signal at all for the scraper to
        /// reconcile onto a Kodi instance that has never played the item. Confirmed live
        /// (2026-09-05): a movie completed on one Shield stayed permanently unwatched on
        /// another, since resolve_progress_direction() saw ResumePositionPercent=null and had
        /// nothing to compare. IsWatched/LastWatchedAt read UserLibrary.Status/CompletedAt
        /// instead, which are never cleared, giving the addon a genuine "when was this
        /// finished" signal to compare against Kodi's own lastplayed the same way it already
        /// compares resume timestamps.</summary>
        bool IsWatched = false,
        DateTime? LastWatchedAt = null
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
        List<ScraperSeasonDto>? Seasons,
        /// <summary>See ScraperMovieDetailsDto.UserRating -- shows have no resume concept
        /// (Kodi tracks that per-episode only), so only rating applies here.</summary>
        int? UserRating = null
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
        string? ThumbUrl,
        ScraperExternalIdsDto? ExternalIds,
        // The parent show's own title/year -- not this episode's -- so the Kodi addon can locate
        // the show's own folder on disk for this episode (see ScraperController.GetEpisodeDetails).
        string? ShowTitle,
        int? ShowYear,
        /// <summary>See ScraperMovieDetailsDto.UserRating/ResumePositionPercent -- episodes
        /// carry both, same as movies.</summary>
        int? UserRating = null,
        double? ResumePositionPercent = null,
        DateTime? ResumeUpdatedAt = null,
        /// <summary>See ScraperMovieDetailsDto.IsWatched/LastWatchedAt -- same reasoning,
        /// per episode rather than per movie.</summary>
        bool IsWatched = false,
        DateTime? LastWatchedAt = null
    );
}
