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
    }
}
