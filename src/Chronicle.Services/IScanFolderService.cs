using Chronicle.Core.Models;

namespace Chronicle.Services;

/// <param name="MediaTypeId">The folder's media type, or null (or 0) to sort each file into its own type.</param>
/// <param name="BundleRelatedFiles">Null follows the global setting.</param>
public record CreateScanFolderRequest(string Path, int? MediaTypeId, bool Recursive, bool? BundleRelatedFiles = null);
public record UpdateScanFolderRequest(string Path, int? MediaTypeId, bool Recursive, bool IsEnabled, bool? BundleRelatedFiles = null);
public record PathValidationResult(bool Valid, string? Error);

public interface IScanFolderService
{
    Task<List<ScanFolder>> GetAllAsync(CancellationToken ct = default);
    Task<ScanFolder> CreateAsync(CreateScanFolderRequest request, CancellationToken ct = default);
    Task<ScanFolder> UpdateAsync(int id, UpdateScanFolderRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<PathValidationResult> ValidatePathAsync(string path, CancellationToken ct = default);
}
