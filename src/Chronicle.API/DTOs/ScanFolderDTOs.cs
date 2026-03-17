using System.ComponentModel.DataAnnotations;

namespace Chronicle.API.DTOs;

public record ScanFolderDto(
    int Id,
    string Path,
    int MediaTypeId,
    string MediaTypeName,
    bool Recursive,
    bool IsEnabled,
    DateTime CreatedAt,
    DateTime? LastScannedAt
);

public record CreateScanFolderDto(
    [Required] string Path,
    [Required] int MediaTypeId,
    bool Recursive = true
);

public record UpdateScanFolderDto(
    [Required] string Path,
    [Required] int MediaTypeId,
    bool Recursive = true,
    bool IsEnabled = true
);

public record ValidatePathDto(
    [Required] string Path
);

public record PathValidationResultDto(
    bool Valid,
    string? Error
);
