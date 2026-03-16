namespace Chronicle.Plugins;

/// <summary>
/// Optional interface for metadata providers that can supply per-season and
/// per-episode data from a TV series.  <see cref="IMetadataProvider"/> plugins
/// that support this interface will be discovered at runtime via a cast, so
/// implementing it is additive and does not break providers that omit it.
/// </summary>
public interface ITvDetailProvider
{
    /// <summary>
    /// Fetches season-level data for a specific season of a TV series.
    /// </summary>
    /// <param name="seriesId">The provider's numeric series ID (e.g. TMDB series ID).</param>
    /// <param name="seasonNumber">The season number (0 = specials).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Season data, or <c>null</c> if not found.</returns>
    Task<TvSeasonDetail?> GetTvSeasonAsync(int seriesId, int seasonNumber, CancellationToken ct = default);

    /// <summary>
    /// Fetches episode-level data for a specific episode of a TV series.
    /// </summary>
    /// <param name="seriesId">The provider's numeric series ID.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="episodeNumber">The episode number within the season.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Episode data, or <c>null</c> if not found.</returns>
    Task<TvEpisodeDetail?> GetTvEpisodeAsync(int seriesId, int seasonNumber, int episodeNumber, CancellationToken ct = default);
}

/// <summary>Season-level metadata returned by <see cref="ITvDetailProvider"/>.</summary>
public class TvSeasonDetail
{
    /// <summary>Provider-internal season ID (e.g. TMDB season_id).</summary>
    public int? SeasonId { get; set; }

    /// <summary>Season number (0 = specials).</summary>
    public int SeasonNumber { get; set; }

    /// <summary>Season name as returned by the provider.</summary>
    public string? Name { get; set; }

    /// <summary>Season overview / synopsis.</summary>
    public string? Overview { get; set; }

    /// <summary>Premiere date (ISO 8601 string, e.g. "2023-09-22").</summary>
    public string? AirDate { get; set; }

    /// <summary>
    /// Poster image path relative to the provider's image base URL
    /// (e.g. "/abc123.jpg").  Full URL: https://image.tmdb.org/t/p/w500{PosterPath}.
    /// </summary>
    public string? PosterPath { get; set; }

    /// <summary>Aggregate vote average from the provider.</summary>
    public double? VoteAverage { get; set; }

    /// <summary>Number of episodes in this season as reported by the provider.</summary>
    public int? EpisodeCount { get; set; }

    /// <summary>All raw fields from the provider response, serialised to JSON.</summary>
    public string? RawJson { get; set; }
}

/// <summary>Episode-level metadata returned by <see cref="ITvDetailProvider"/>.</summary>
public class TvEpisodeDetail
{
    /// <summary>Season number this episode belongs to.</summary>
    public int SeasonNumber { get; set; }

    /// <summary>Episode number within the season.</summary>
    public int EpisodeNumber { get; set; }

    /// <summary>Episode title as returned by the provider.</summary>
    public string? Name { get; set; }

    /// <summary>Episode synopsis.</summary>
    public string? Overview { get; set; }

    /// <summary>Original air date (ISO 8601 string).</summary>
    public string? AirDate { get; set; }

    /// <summary>
    /// Still / thumbnail image path relative to the provider's image base URL
    /// (e.g. "/xyz.jpg").  Full URL: https://image.tmdb.org/t/p/w500{StillPath}.
    /// </summary>
    public string? StillPath { get; set; }

    /// <summary>Episode vote average.</summary>
    public double? VoteAverage { get; set; }

    /// <summary>Runtime in minutes if reported separately from the show default.</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>Guest stars (display names).</summary>
    public List<string> GuestStars { get; set; } = [];

    /// <summary>Crew members in director / writer roles.</summary>
    public List<string> Crew { get; set; } = [];

    /// <summary>All raw fields from the provider response, serialised to JSON.</summary>
    public string? RawJson { get; set; }
}
