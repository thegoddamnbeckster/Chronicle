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
        string? MediaType = null
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
        string? MediaType = null
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
    }
}
