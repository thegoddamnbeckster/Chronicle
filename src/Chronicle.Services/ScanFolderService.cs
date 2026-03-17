using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

public class ScanFolderService : IScanFolderService
{
    private readonly ChronicleDbContext _db;

    public ScanFolderService(ChronicleDbContext db) => _db = db;

    public Task<List<ScanFolder>> GetAllAsync(CancellationToken ct = default) =>
        _db.ScanFolders.Include(f => f.MediaType).OrderBy(f => f.Path).ToListAsync(ct);

    public async Task<ScanFolder> CreateAsync(CreateScanFolderRequest request, CancellationToken ct = default)
    {
        var folder = new ScanFolder
        {
            Path        = request.Path,
            MediaTypeId = request.MediaTypeId,
            Recursive   = request.Recursive,
            IsEnabled   = true,
            CreatedAt   = DateTime.UtcNow,
        };
        _db.ScanFolders.Add(folder);
        await _db.SaveChangesAsync(ct);
        await _db.Entry(folder).Reference(f => f.MediaType).LoadAsync(ct);
        return folder;
    }

    public async Task<ScanFolder> UpdateAsync(int id, UpdateScanFolderRequest request, CancellationToken ct = default)
    {
        var folder = await _db.ScanFolders.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Scan folder {id} not found.");
        folder.Path        = request.Path;
        folder.MediaTypeId = request.MediaTypeId;
        folder.Recursive   = request.Recursive;
        folder.IsEnabled   = request.IsEnabled;
        await _db.SaveChangesAsync(ct);
        await _db.Entry(folder).Reference(f => f.MediaType).LoadAsync(ct);
        return folder;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var folder = await _db.ScanFolders.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Scan folder {id} not found.");
        _db.ScanFolders.Remove(folder);
        await _db.SaveChangesAsync(ct);
    }

    public Task<PathValidationResult> ValidatePathAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(new PathValidationResult(false, "Path cannot be empty."));

            if (!Directory.Exists(path))
                return Task.FromResult(new PathValidationResult(false,
                    $"Directory does not exist or is not accessible: {path}"));

            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
            return Task.FromResult(new PathValidationResult(true, null));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new PathValidationResult(false,
                $"Chronicle does not have permission to read: {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new PathValidationResult(false, ex.Message));
        }
    }
}
