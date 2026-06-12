namespace Chronicle.Services
{
    // ── Existing scan-and-import flow ─────────────────────────────────────────

    public record FileScanRequest(
        string Path,
        bool Recursive,
        int MediaTypeId,
        int ConfidenceThreshold = 80
    );

    public record FileScanSummary(
        int Added,
        int Skipped,
        int AlreadyInLibrary,
        List<SkippedFile> SkippedFiles
    );

    public record SkippedFile(
        string FilePath,
        string ParsedTitle,
        int ConfidenceScore
    );

    // ── Preview (scan without importing) ─────────────────────────────────────

    public record ScanPreviewRequest(
        string Path,
        bool Recursive,
        int MediaTypeId
    );

    public record ScannedFileResult(
        string FilePath,
        string ParsedTitle,
        int? ParsedYear,
        int ConfidenceScore,
        string? SuggestedExternalId,
        string MediaTypeHint
    );

    public record ScanPreview(
        List<ScannedFileResult> Files
    );

    // ── Identify (match scanned files against a metadata provider) ────────────

    public record IdentifyRequest(
        List<ScannedFileResult> Files,
        int MediaTypeId
    );

    public record MetadataCandidate(
        string ExternalId,
        string Title,
        int? Year,
        string? PosterUrl,
        string? Overview,
        double? Rating,
        int MatchScore,
        string? Source = null,
        List<string>? Genres = null,
        List<string>? Cast = null
    );

    public record FileIdentification(
        ScannedFileResult File,
        List<MetadataCandidate> Candidates
    );

    public record IdentifyResult(
        List<FileIdentification> Results
    );

    // ── Import approved matches ───────────────────────────────────────────────

    public record ImportApproval(
        string FilePath,
        string ExternalId
    );

    public record ImportApprovedRequest(
        List<ImportApproval> Approvals,
        int MediaTypeId,
        int UserId
    );

    public record ImportApprovedSummary(
        int Imported,
        int Failed,
        List<string> Failures,
        int Duplicates = 0
    );

    // ── Direct import (scanner data only — no metadata provider required) ────────

    /// <summary>
    /// One file to import directly from scanner data, without a prior TMDB lookup.
    /// The background MetadataRefreshService will enrich it with TMDB data automatically.
    /// </summary>
    public record DirectImportFile(
        string FilePath,
        string ParsedTitle,
        int? ParsedYear,
        string? SuggestedExternalId,
        string MediaTypeHint,
        // Hierarchy fields (populated by FileScanner v1.1.0+)
        string? ShowTitle = null,
        int? SeasonNumber = null,
        int? EpisodeNumber = null,
        string? EpisodeTitle = null,
        int? AudioTrackNumber = null);

    public record DirectImportRequest(
        List<DirectImportFile> Files,
        int MediaTypeId,
        int UserId
    );

    // ── Grouped import (hierarchical scanner) ────────────────────────────────

    public record ImportGroupsRequest(
        List<ScanGroupImport> Groups,
        int MediaTypeId);

    public record ScanGroupImport(
        string Name,
        int? Year,
        string? PosterPath,
        List<ScanGroupImport> Children,
        List<string> Files,
        string? FolderPath = null,
        int? Number = null)
    {
        /// <summary>Total file count across this group and all descendants.</summary>
        public int TotalFileCount => Files.Count + Children.Sum(c => c.TotalFileCount);
    }

}
