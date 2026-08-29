using Chronicle.Core.Models;

namespace Chronicle.Services
{
    public record ScrobbleRequest(
        /// <summary>
        /// Already-resolved Chronicle media item ID. Null when the caller (e.g. the Kodi
        /// addon) knows an item only by external ID / title — see <see cref="ExternalIds"/>.
        /// </summary>
        int? MediaItemId,
        double? ProgressPercent,
        DateTime? Timestamp,
        string? DeviceName,
        /// <summary>
        /// Cross-reference IDs (e.g. {"imdb": "tt1234567", "tmdb": "603"}) used to resolve
        /// or create the media item when <see cref="MediaItemId"/> is not supplied.
        /// </summary>
        IReadOnlyDictionary<string, string>? ExternalIds = null,
        string? Title = null,
        int? Year = null,
        /// <summary>"movie" | "tv_episode" | "tv_show" | "track" — used only to pick a
        /// media type when a stub item must be created. Defaults to "movie".</summary>
        string? MediaType = null,
        /// <summary>
        /// Present only when MediaType is "episode" — resolves the scrobble onto the
        /// actual episode item in Chronicle's existing show/season/episode hierarchy
        /// instead of the show itself. Per-user report (2026-08-29): "you're missing
        /// the episode name" -- Chronicle's scrobble model used to match at the show
        /// level only, by design, discarding season/episode entirely even when the
        /// real episode already existed in the library. See
        /// MediaItemMatcher.FindOrCreateEpisodeAsync.
        /// </summary>
        int? Season = null,
        int? Episode = null,
        /// <summary>Fallback name only, used solely when Chronicle has to create a new
        /// episode item (no existing one found at that season/episode number) -- never
        /// overwrites a real title an existing episode already has.</summary>
        string? EpisodeTitle = null
    );

    public record ScrobbleResult(
        InteractionEvent Event,
        bool MarkedAsWatched
    );

    /// <summary>
    /// Identifies an item to check for a stored resume position, the same way a
    /// <see cref="ScrobbleRequest"/> identifies one to scrobble -- MediaItemId when
    /// already known, otherwise ExternalIds/Title/Year/MediaType to resolve it. Never
    /// creates a stub if nothing matches (unlike scrobbling) -- see
    /// ScrobbleService.TryFindMediaItemAsync.
    /// </summary>
    public record ResumeLookupRequest(
        int? MediaItemId,
        IReadOnlyDictionary<string, string>? ExternalIds = null,
        string? Title = null,
        int? Year = null,
        string? MediaType = null,
        /// <summary>Same episode-hierarchy resolution ScrobbleRequest uses -- required
        /// so a resume check lands on the same item id a scrobble for the same episode
        /// would (the show's own UserLibrary row won't have the episode's resume
        /// position once ScrobbleRequest.Season/Episode start resolving scrobbles onto
        /// the episode instead of the show). Never creates anything if missing, unlike
        /// scrobbling -- see MediaItemMatcher.FindEpisodeAsync.</summary>
        int? Season = null,
        int? Episode = null
    );

    /// <summary>
    /// A stored cross-device resume position. ResumePositionPercent is percent-of-
    /// duration, not raw seconds -- see UserLibrary.ResumePositionPercent's own doc for
    /// why percent is the portable unit across devices/encodes.
    /// </summary>
    public record ResumeState(
        int MediaItemId,
        double ResumePositionPercent,
        DateTime? ResumeUpdatedAt
    );

    /// <summary>
    /// Identifies an item to rate the same way a <see cref="ScrobbleRequest"/> identifies
    /// one to scrobble -- MediaItemId when already known, otherwise ExternalIds/Title/
    /// Year/MediaType to resolve it. Never creates a stub if nothing matches (same as
    /// <see cref="ResumeLookupRequest"/>) -- a rating always arrives after a scrobble
    /// session for the same item, so the item is expected to already exist.
    /// </summary>
    public record RateRequest(
        int? MediaItemId,
        int Rating,
        IReadOnlyDictionary<string, string>? ExternalIds = null,
        string? Title = null,
        int? Year = null,
        string? MediaType = null
    );

    public record RateResult(int MediaItemId, int Rating);

    /// <summary>
    /// One currently-live playback session, inferred from scrobble recency rather than any
    /// explicit start/stop signal (the scrobble protocol has none — see ScrobbleService's own
    /// doc on GetActiveSessionsAsync for why a staleness window is the correct proxy).
    /// </summary>
    public record ActiveSession(
        int MediaItemId,
        string MediaItemName,
        string? PosterUrl,
        double ProgressPercent,
        int? ElapsedMinutes,
        int? RuntimeMinutes,
        string? DeviceName,
        DateTime LastUpdatedAt,
        int? UserRating = null,
        /// <summary>Null for anything that isn't a TV episode -- movies have no season/episode
        /// to show. Per-user request (2026-08-29): the Now Playing banner should show the
        /// episode's own season/episode numbers alongside its name, not just the name, since
        /// that's "obviously different than the movie version" of the same banner.</summary>
        int? Season = null,
        int? Episode = null
    );

    public interface IScrobbleService
    {
        Task<ScrobbleResult> ScrobbleAsync(int userId, ScrobbleRequest request, CancellationToken ct = default);
        Task<IEnumerable<InteractionEvent>> GetHistoryAsync(
            int userId, int page = 1, int perPage = 20, int? mediaItemId = null);

        /// <summary>
        /// Most recent MarkedAsWatched=true event timestamp and total watched-event count
        /// for one item — the pair a sync client needs to reconcile play count/last-played
        /// against another system's own count, without paging through full history.
        /// </summary>
        Task<(DateTime? LastWatchedAt, int WatchedCount)> GetWatchSummaryAsync(
            int userId, int mediaItemId, CancellationToken ct = default);

        /// <summary>
        /// Null if the item can't be resolved at all, or resolves but has nothing to
        /// resume (never scrobbled, or already fully watched) -- see ResumeState's own
        /// doc for why a client doesn't need to distinguish those cases.
        /// </summary>
        Task<ResumeState?> GetResumeStateAsync(int userId, ResumeLookupRequest request, CancellationToken ct = default);

        /// <summary>
        /// Sets/overwrites the caller's UserRating (1-10) for an item -- the same field
        /// the web UI's "Your Rating" dropdown edits, so a rating submitted from a Kodi
        /// addon (e.g. the SIMKL-style post-playback prompt) and one set from the browser
        /// are indistinguishable afterward, same field, same place. Throws
        /// <see cref="Chronicle.Core.Exceptions.MediaNotFoundException"/> if the item
        /// can't be resolved, ArgumentException if Rating is outside 1-10.
        /// </summary>
        Task<RateResult> RateAsync(int userId, RateRequest request, CancellationToken ct = default);

        /// <summary>
        /// One entry per device currently believed to be actively playing something for this
        /// user — see ScrobbleService's implementation doc for the staleness-window inference.
        /// Devices with no recent scrobble, or whose most recent event already crossed the
        /// watched threshold, are simply absent — never returned as a "finished" entry.
        /// </summary>
        Task<IReadOnlyList<ActiveSession>> GetActiveSessionsAsync(int userId, CancellationToken ct = default);
    }
}
