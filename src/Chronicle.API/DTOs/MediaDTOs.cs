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

    public record ExternalIdDto(string Source, string ExternalId);

    /// <summary>Rich metadata fetched from TMDB, stored in MetadataJson and surfaced here.</summary>
    public record TmdbMetaDto(
        double? Rating,
        List<string> Genres,
        List<string> Cast,
        List<string> Directors,
        string? PosterUrl,
        string? BackdropUrl
    );

    /// <summary>Local file data discovered by the File Scanner plugin.</summary>
    public record FileScannerMetaDto(
        string? FilePath,
        string? LocalPosterPath,
        string? NfoPosterUrl
    );

    public record RefreshLogDto(
        string ProviderName,
        DateTime RefreshedAt,
        bool Succeeded,
        string? ErrorMessage
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
        DateTime UpdatedAt,
        List<ExternalIdDto> ExternalIds,
        TmdbMetaDto? TmdbMeta = null,
        FileScannerMetaDto? FileScannerMeta = null,
        List<RefreshLogDto>? RefreshLogs = null
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

    public record MediaTypeDto(int Id, string Name, string DisplayName);

    /// <summary>Body for POST /api/v1/media/{id}/reidentify.</summary>
    public record ReidentifyRequestDto(
        [Required] string Input
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

    public record NuclearResetRequestDto(string ConfirmationToken);
}
