namespace Chronicle.Services
{
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
}
