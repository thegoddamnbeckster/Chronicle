using Chronicle.Core.Models;

namespace Chronicle.Services;

public record CreateScanFolderRequest(string Path, int MediaTypeId, bool Recursive);
public record UpdateScanFolderRequest(string Path, int MediaTypeId, bool Recursive, bool IsEnabled);
public record PathValidationResult(bool Valid, string? Error);

public interface IScanFolderService
{
    Task<List<ScanFolder>> GetAllAsync(CancellationToken ct = default);
    Task<ScanFolder> CreateAsync(CreateScanFolderRequest request, CancellationToken ct = default);
    Task<ScanFolder> UpdateAsync(int id, UpdateScanFolderRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<PathValidationResult> ValidatePathAsync(string path, CancellationToken ct = default);
}
