using System.ComponentModel.DataAnnotations;
using System.Text.Json;
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
        List<string>? Genres,
        List<string>? Cast,
        List<string>? Directors,
        string? PosterUrl,
        string? BackdropUrl,
        /// <summary>
        /// Season poster image path from TMDB (e.g. "/abc.jpg").
        /// Full URL: https://image.tmdb.org/t/p/w500{PosterPath}
        /// Populated for season-level items refreshed via ITvDetailProvider.
        /// </summary>
        string? PosterPath = null,
        /// <summary>
        /// Episode still / thumbnail image path from TMDB (e.g. "/xyz.jpg").
        /// Full URL: https://image.tmdb.org/t/p/w500{StillPath}
        /// Populated for episode-level items refreshed via ITvDetailProvider.
        /// </summary>
        string? StillPath = null,
        /// <summary>Vote average from TMDB (season or episode level).</summary>
        double? VoteAverage = null,
        /// <summary>Air date string (ISO 8601) for seasons and episodes.</summary>
        string? AirDate = null,
        /// <summary>Number of episodes in a season, as reported by TMDB.</summary>
        int? EpisodeCount = null,
        /// <summary>Guest star names for this episode.</summary>
        List<string>? GuestStars = null,
        /// <summary>Crew names (directors/writers) for this episode.</summary>
        List<string>? Crew = null
    );

    /// <summary>Local file data discovered by the File Scanner plugin.</summary>
    public record FileScannerMetaDto(
        string? FilePath,
        string? LocalPosterPath,
        string? NfoPosterUrl,
        DateTime? ImportedAt = null
    );

    public record RefreshLogDto(
        string ProviderName,
        DateTime RefreshedAt,
        bool Succeeded,
        string? ErrorMessage
    );

    public record AncestorDto(int Id, string Name);

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
        /// <summary>
        /// All non-TMDB, non-fileScanner plugin metadata keyed by plugin ID
        /// (e.g. "chronicle.plugin.musicbrainz", "chronicle.plugin.omdb").
        /// Values are raw JSON so any plugin's data passes through without the API
        /// needing to know the shape of each plugin's metadata.
        /// </summary>
        Dictionary<string, JsonElement>? PluginMetadata = null,
        List<RefreshLogDto>? RefreshLogs = null,
        List<AncestorDto>? Ancestors = null
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
