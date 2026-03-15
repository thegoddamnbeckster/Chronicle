using Chronicle.API.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/filesystem")]
[Authorize]
public class FilesystemController : ControllerBase
{
    /// <summary>
    /// Returns the immediate subdirectories of <paramref name="path"/>.
    /// When <paramref name="path"/> is null or empty, returns logical drive roots
    /// (Windows: C:\, D:\, etc. — Linux/Docker: mount points such as /).
    /// Access-denied subdirectories are silently skipped rather than erroring.
    /// </summary>
    [HttpGet]
    public IActionResult List([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Ok(ApiResponse<FilesystemListingDto>.Ok(GetDriveRoots()));

        var dir = new DirectoryInfo(path);

        // Don't pre-check dir.Exists — network shares (e.g. NAS) may report
        // non-existent while sleeping but become accessible on first access.
        // Let EnumerateDirectories trigger the wakeup and surface real errors.
        string? parent = dir.Parent?.FullName;

        var subdirs = new List<FilesystemEntryDto>();
        try
        {
            foreach (var sub in dir.EnumerateDirectories())
            {
                subdirs.Add(new FilesystemEntryDto(sub.Name, sub.FullName));
            }
        }
        catch (DirectoryNotFoundException)
        {
            return BadRequest(ApiResponse<FilesystemListingDto>.Fail(
                "PATH_NOT_FOUND", $"Directory not found: {path}"));
        }
        catch (UnauthorizedAccessException)
        {
            return BadRequest(ApiResponse<FilesystemListingDto>.Fail(
                "ACCESS_DENIED", $"Access denied: {path}"));
        }
        catch (Exception ex) when (ex is IOException or PathTooLongException or System.Security.SecurityException)
        {
            return BadRequest(ApiResponse<FilesystemListingDto>.Fail(
                "FILESYSTEM_ERROR", ex.Message));
        }

        subdirs.Sort((a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return Ok(ApiResponse<FilesystemListingDto>.Ok(
            new FilesystemListingDto(dir.FullName, parent, subdirs)));
    }

    private static FilesystemListingDto GetDriveRoots()
    {
        // Include network drives even when IsReady=false — mapped NAS shares
        // report not-ready while sleeping but the mapping still exists.
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady || d.DriveType == DriveType.Network)
            .Select(d => new FilesystemEntryDto(d.Name, d.RootDirectory.FullName))
            .ToList();

        return new FilesystemListingDto(null, null, drives);
    }
}
