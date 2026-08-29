using System.ComponentModel.DataAnnotations;

namespace Chronicle.API.DTOs
{
    public record ScrobbleRequestDto(
        /// <summary>Already-resolved Chronicle media item ID. Omit and supply
        /// Title/ExternalIds instead when the caller doesn't have one yet.</summary>
        int? MediaItemId,
        [Range(0, 100)] double? ProgressPercent,
        DateTime? Timestamp,
        string? DeviceName,
        Dictionary<string, string>? ExternalIds = null,
        string? Title = null,
        int? Year = null,
        string? MediaType = null
    );

    public record ScrobbleResponseDto(
        int EventId,
        int MediaItemId,
        double? ProgressPercent,
        DateTime Timestamp,
        bool MarkedAsWatched,
        string? DeviceName
    );

    public record WatchSummaryDto(
        DateTime? LastWatchedAt,
        int WatchedCount
    );

    /// <summary>Same identifying shape as ScrobbleRequestDto minus progress -- a client
    /// checking "is there something to resume" supplies whatever it would've supplied
    /// to scrobble this item.</summary>
    public record ResumeLookupRequestDto(
        int? MediaItemId,
        Dictionary<string, string>? ExternalIds = null,
        string? Title = null,
        int? Year = null,
        string? MediaType = null
    );

    public record ResumeStateDto(
        int MediaItemId,
        double ResumePositionPercent,
        DateTime? ResumeUpdatedAt
    );

    /// <summary>Same identifying shape as ScrobbleRequestDto minus progress -- a client
    /// rating an item supplies whatever it would've supplied to scrobble/resume-check
    /// that same item.</summary>
    public record RateRequestDto(
        int? MediaItemId,
        [Range(1, 10)] int Rating,
        Dictionary<string, string>? ExternalIds = null,
        string? Title = null,
        int? Year = null,
        string? MediaType = null
    );

    public record RateResponseDto(
        int MediaItemId,
        int Rating
    );

    /// <summary>One currently-live playback session for the "Now Playing" banner — see
    /// ScrobbleService.GetActiveSessionsAsync for how "actively playing" is inferred.</summary>
    public record ActiveSessionDto(
        int MediaItemId,
        string MediaItemName,
        string? PosterUrl,
        double ProgressPercent,
        /// <summary>Null when the item has no known runtime — the UI shows percentage only.</summary>
        int? ElapsedMinutes,
        int? RuntimeMinutes,
        string? DeviceName,
        DateTime LastUpdatedAt,
        List<AncestorDto>? Ancestors = null,
        /// <summary>The caller's own 1-10 rating for this item, if set — shown as a badge
        /// on the Now Playing banner. Null, not 0, when unrated.</summary>
        int? UserRating = null
    );

    public record HistoryItemDto(
        int Id,
        int MediaItemId,
        string MediaItemName,
        double? ProgressPercent,
        DateTime Timestamp,
        bool MarkedAsWatched,
        string? DeviceName,
        /// <summary>Root-first parent context (e.g. [Show, Season] for an episode) so the
        /// UI can show "Show › Season › Episode" instead of just the episode's bare name,
        /// which for scanned TV is often a generic code like "S28E11".</summary>
        List<AncestorDto>? Ancestors = null,
        /// <summary>True when Timestamp is a borrowed fallback (e.g. a SIMKL-imported
        /// episode stamped with its show's last-watched date), not this item's own real
        /// watch time -- the UI should mark it as approximate rather than exact.</summary>
        bool IsApproximateTimestamp = false
    );
}
