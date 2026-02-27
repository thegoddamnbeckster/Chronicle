using System.ComponentModel.DataAnnotations;

namespace Chronicle.API.DTOs
{
    public record ScrobbleRequestDto(
        [Required] int MediaItemId,
        [Range(0, 100)] double? ProgressPercent,
        DateTime? Timestamp,
        string? DeviceName
    );

    public record ScrobbleResponseDto(
        int EventId,
        int MediaItemId,
        double? ProgressPercent,
        DateTime Timestamp,
        bool MarkedAsWatched,
        string? DeviceName
    );

    public record HistoryItemDto(
        int Id,
        int MediaItemId,
        string MediaItemName,
        double? ProgressPercent,
        DateTime Timestamp,
        bool MarkedAsWatched,
        string? DeviceName
    );
}
