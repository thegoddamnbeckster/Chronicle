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

        if (!dir.Exists)
            return BadRequest(ApiResponse<FilesystemListingDto>.Fail(
                "PATH_NOT_FOUND", $"Directory not found: {path}"));

        string? parent = dir.Parent?.FullName;

        var subdirs = new List<FilesystemEntryDto>();
        try
        {
            foreach (var sub in dir.EnumerateDirectories())
            {
                subdirs.Add(new FilesystemEntryDto(sub.Name, sub.FullName));
            }
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
        // Include a drive if it's ready OR if it's a network drive (mapped
        // network shares report IsReady=false when the NAS is sleeping but the
        // mapping still exists; Windows wakes the share on first access).
        // This intentionally excludes empty CD-ROM/removable slots.
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady || d.DriveType == DriveType.Network)
            .Select(d => new FilesystemEntryDto(d.Name, d.RootDirectory.FullName))
            .ToList();

        return new FilesystemListingDto(null, null, drives);
    }
}
