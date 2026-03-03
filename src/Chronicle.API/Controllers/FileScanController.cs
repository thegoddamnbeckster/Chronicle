using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/scan")]
[Authorize]
public class FileScanController : ControllerBase
{
    private readonly IFileScanService _scanService;

    public FileScanController(IFileScanService scanService)
    {
        _scanService = scanService;
    }

    /// <summary>
    /// Returns whether a file scanner plugin is loaded and which media types it supports.
    /// The frontend uses this to conditionally show the Scan page in the navigation.
    /// </summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStatus()
    {
        var (available, names) = await _scanService.GetStatusAsync();
        return Ok(ApiResponse<FileScanStatusDto>.Ok(new FileScanStatusDto(available, names)));
    }

    /// <summary>
    /// Scans a local directory for media files and adds matching items to the user's library.
    /// Files with a confidence score below the threshold are reported but not added.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RunScan(
        [FromBody] FileScanRequestDto dto,
        CancellationToken ct)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub");
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized(ApiResponse<FileScanSummaryDto>.Fail("UNAUTHORIZED", "User identity could not be determined."));

        var request = new FileScanRequest(
            dto.Path,
            dto.Recursive,
            dto.MediaTypeId,
            dto.ConfidenceThreshold
        );

        try
        {
            var summary = await _scanService.ScanAsync(request, userId, ct);
            var result = new FileScanSummaryDto(
                summary.Added,
                summary.Skipped,
                summary.AlreadyInLibrary,
                summary.SkippedFiles.Select(f => new SkippedFileDto(f.FilePath, f.ParsedTitle, f.ConfidenceScore)).ToList()
            );
            return Ok(ApiResponse<FileScanSummaryDto>.Ok(result));
        }
        catch (DirectoryNotFoundException ex)
        {
            return BadRequest(ApiResponse<FileScanSummaryDto>.Fail("DIRECTORY_NOT_FOUND", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<FileScanSummaryDto>.Fail("SCAN_ERROR", ex.Message));
        }
    }
}
