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
}
