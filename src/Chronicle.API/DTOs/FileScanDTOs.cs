using System.ComponentModel.DataAnnotations;

namespace Chronicle.API.DTOs
{
    public record FileScanRequestDto(
        [Required] string Path,
        bool Recursive = true,
        [Required] int MediaTypeId = 0,
        int ConfidenceThreshold = 80
    );

    public record FileScanSummaryDto(
        int Added,
        int Skipped,
        int AlreadyInLibrary,
        List<SkippedFileDto> SkippedFiles
    );

    public record SkippedFileDto(
        string FilePath,
        string ParsedTitle,
        int ConfidenceScore
    );

    public record FileScanStatusDto(
        bool Available,
        string[] SupportedMediaTypeNames
    );

    // ── Preview ───────────────────────────────────────────────────────────────

    public record ScanPreviewRequestDto(
        [Required] string Path,
        bool Recursive = true,
        [Required] int MediaTypeId = 0
    );

    public record ScannedFileDto(
        string FilePath,
        string ParsedTitle,
        int? ParsedYear,
        int ConfidenceScore,
        string? SuggestedExternalId,
        string MediaTypeHint
    );

    public record ScanPreviewDto(
        List<ScannedFileDto> Files
    );

    // ── Identify ──────────────────────────────────────────────────────────────

    public record IdentifyRequestDto(
        [Required] List<ScannedFileDto> Files,
        [Required] int MediaTypeId
    );

    public record MetadataCandidateDto(
        string ExternalId,
        string Title,
        int? Year,
        string? PosterUrl,
        string? Overview,
        double? Rating,
        int MatchScore
    );

    public record FileIdentificationDto(
        ScannedFileDto File,
        List<MetadataCandidateDto> Candidates
    );

    public record IdentifyResultDto(
        List<FileIdentificationDto> Results
    );

    // ── Import approved ───────────────────────────────────────────────────────

    public record ImportApprovalDto(
        [Required] string FilePath,
        [Required] string ExternalId
    );

    public record ImportRequestDto(
        [Required] List<ImportApprovalDto> Approvals,
        [Required] int MediaTypeId
    );

    public record ImportSummaryDto(
        int Imported,
        int Failed,
        List<string> Failures,
        int Duplicates = 0
    );

    // ── Direct import (scanner data only) ────────────────────────────────────

    public record DirectImportFileDto(
        [Required] string FilePath,
        [Required] string ParsedTitle,
        int? ParsedYear,
        string? SuggestedExternalId,
        string MediaTypeHint = "movie",
        string? ShowTitle = null,
        int? SeasonNumber = null,
        int? EpisodeNumber = null,
        string? EpisodeTitle = null,
        int? AudioTrackNumber = null);

    public record DirectImportRequestDto(
        [Required] List<DirectImportFileDto> Files,
        [Required] int MediaTypeId
    );

    // ── Add from search ───────────────────────────────────────────────────────

    public record AddFromSearchDto(
        [Required] string ExternalId,
        [Required] int MediaTypeId
    );

    // ── Scan progress ──────────────────────────────────────────────────────────

    /// <summary>
    /// Real-time snapshot of an in-progress directory scan.
    /// Polled by the frontend every 500 ms while a preview scan is running.
    /// </summary>
    public record ScanProgressDto(
        bool IsScanning,
        string? CurrentFolder,
        int FoldersScanned,
        int TotalFolders,
        int FilesFound
    );

    // ── Import progress ───────────────────────────────────────────────────────

    /// <summary>
    /// Real-time snapshot of an in-progress (or recently completed) import-groups operation.
    /// Polled by the frontend every 500 ms while IsRunning is true.
    /// </summary>
    public record ImportProgressDto(
        bool IsRunning,
        bool IsComplete,
        int Total,
        int Processed,
        string? CurrentItemName,
        string? StatusMessage,
        string? Error,
        ImportSummaryDto? Result
    );

    // ── Grouped preview / import ───────────────────────────────────────────────

    public record ScanGroupDto(
        string GroupKey,
        string Name,
        int HierarchyLevel,
        int? Year,
        int? Number,
        string? PosterPath,
        int ConfidenceScore,       // 0–100
        List<string> SignalSources,
        bool HasConflicts,
        List<ScanGroupDto> Children,
        List<string> Files,
        string? FolderPath = null);

    public record ScanGroupResultDto(
        List<ScanGroupDto> Groups,
        List<string> Ungrouped,
        int TotalFiles);

    public record ImportGroupsRequestDto(
        List<ImportGroupDto> Groups,
        int MediaTypeId);

    public record ImportGroupDto(
        string Name,
        int? Year,
        int? Number,
        string? PosterPath,
        List<ImportGroupDto> Children,
        List<string> Files,
        string? FolderPath = null);
}
