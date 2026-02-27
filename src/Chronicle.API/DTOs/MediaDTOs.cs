using System.ComponentModel.DataAnnotations;
using Chronicle.Core.Models;

namespace Chronicle.API.DTOs
{
    public record CreateMediaItemRequest(
        [Required] int MediaTypeId,
        int? ParentId,
        [Required, MaxLength(500)] string Name,
        int? Year,
        string? Overview,
        string? PosterUrl,
        int? RuntimeMinutes,
        int HierarchyLevel = 0,
        int? Number = null
    );

    public record UpdateMediaItemRequest(
        string? Name,
        int? Year,
        string? Overview,
        string? PosterUrl,
        int? RuntimeMinutes
    );

    public record MediaItemDto(
        int Id,
        int MediaTypeId,
        string MediaTypeName,
        int? ParentId,
        string Name,
        int? Year,
        string? Overview,
        string? PosterUrl,
        int? RuntimeMinutes,
        int HierarchyLevel,
        int? Number,
        DateTime CreatedAt,
        DateTime UpdatedAt
    );

    public record AddToLibraryRequestDto(
        [Required] int MediaItemId,
        string Status = "PlanToWatch"
    );

    public record UpdateLibraryRequestDto(
        string? Status,
        int? UserRating,
        string? Notes
    );

    public record LibraryEntryDto(
        int Id,
        int UserId,
        MediaItemDto MediaItem,
        string Status,
        int? UserRating,
        string? Notes,
        DateTime AddedAt,
        DateTime UpdatedAt,
        DateTime? StartedAt,
        DateTime? CompletedAt
    );
}
