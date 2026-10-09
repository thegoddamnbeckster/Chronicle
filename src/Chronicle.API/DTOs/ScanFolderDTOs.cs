using System.ComponentModel.DataAnnotations;

namespace Chronicle.API.DTOs;

public record ScanFolderDto(
    int Id,
    string Path,
    int? MediaTypeId,
    string MediaTypeName,
    bool Recursive,
    bool IsEnabled,
    DateTime CreatedAt,
    DateTime? LastScannedAt,
    bool? BundleRelatedFiles = null
);

/// <param name="MediaTypeId">Null or 0 = sort each file into its own media type.</param>
public record CreateScanFolderDto(
    [Required] string Path,
    int? MediaTypeId,
    bool Recursive = true,
    bool? BundleRelatedFiles = null
);

public record UpdateScanFolderDto(
    [Required] string Path,
    int? MediaTypeId,
    bool Recursive = true,
    bool IsEnabled = true,
    bool? BundleRelatedFiles = null
);

public record ValidatePathDto(
    [Required] string Path
);

public record PathValidationResultDto(
    bool Valid,
    string? Error
);
