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
        /// <summary>True when Timestamp was not reported per-item by the source (e.g. a whole
        /// SIMKL show bulk-marked "completed" shares one last-watched date across every
        /// episode) -- not that specific item's own genuine watch time.</summary>
        bool TimestampIsApproximate = false
    );
}
