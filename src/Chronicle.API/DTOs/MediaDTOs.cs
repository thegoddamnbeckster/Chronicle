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

    public record ChangeMediaTypeRequest([Required] int MediaTypeId);

    public record ExternalIdDto(string Source, string ExternalId);

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
        FileScannerMetaDto? FileScannerMeta = null,
        /// <summary>
        /// All plugin metadata keyed by full plugin ID (e.g. "chronicle.plugin.tmdb",
        /// "chronicle.plugin.musicbrainz"). Values are raw JSON so any plugin's data
        /// passes through without the API needing to know the shape of each plugin's metadata.
        /// </summary>
        Dictionary<string, JsonElement>? PluginMetadata = null,
        List<RefreshLogDto>? RefreshLogs = null,
        List<AncestorDto>? Ancestors = null,
        /// <summary>
        /// Enrichment attempt status per plugin, keyed by plugin ID.
        /// Present even when PluginMetadata has no entry for that plugin (e.g. NotFound).
        /// Values: "Pending", "Completed", "NotFound", "Failed", "Exhausted".
        /// </summary>
        Dictionary<string, string>? EnrichmentStatuses = null,
        /// <summary>
        /// The canonical internal name of the media type (e.g. "tv", "movies", "music").
        /// Used for plugin compatibility checks. Distinct from <see cref="MediaTypeName"/>
        /// which carries the user-facing display name (e.g. "TV Shows").
        /// </summary>
        string? MediaTypeInternalName = null,
        /// <summary>
        /// True when this item or any of its descendants has an associated physical file
        /// (i.e. the fileScanner metadata key contains a non-empty filePaths array or a filePath string).
        /// </summary>
        bool HasPhysicalFile = false,
        /// <summary>
        /// True when this item or any leaf in its subtree lacks a physical file.
        /// Covers two cases: (1) purely metadata-only — no descendant has a file;
        /// (2) mixed state — at least one leaf has a file and at least one does not.
        /// Both cases indicate incomplete file coverage and warrant the cloud icon.
        /// </summary>
        bool HasMetadataOnly = false
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

    public record MediaTypeDto(int Id, string Name, string DisplayName, int HierarchyLevels);

    /// <summary>Optional body for POST /api/v1/media/{id}/refresh/{pluginId}.</summary>
    public record PluginRefreshRequestDto(string? Input = null);

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
