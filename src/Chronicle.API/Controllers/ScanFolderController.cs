using Chronicle.API.DTOs;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/scan-folders")]
[Authorize]
public class ScanFolderController : ControllerBase
{
    private readonly IScanFolderService _svc;

    public ScanFolderController(IScanFolderService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var folders = await _svc.GetAllAsync(ct);
        return Ok(ApiResponse<List<ScanFolderDto>>.Ok(folders.Select(ToDto).ToList()));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateScanFolderDto dto, CancellationToken ct)
    {
        var validation = await _svc.ValidatePathAsync(dto.Path, ct);
        if (!validation.Valid)
            return BadRequest(ApiResponse<ScanFolderDto>.Fail("INVALID_PATH", validation.Error!));

        var folder = await _svc.CreateAsync(new(dto.Path, dto.MediaTypeId, dto.Recursive), ct);
        return Created($"/api/v1/scan-folders/{folder.Id}", ApiResponse<ScanFolderDto>.Ok(ToDto(folder)));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateScanFolderDto dto, CancellationToken ct)
    {
        var validation = await _svc.ValidatePathAsync(dto.Path, ct);
        if (!validation.Valid)
            return BadRequest(ApiResponse<ScanFolderDto>.Fail("INVALID_PATH", validation.Error!));

        try
        {
            var folder = await _svc.UpdateAsync(id,
                new(dto.Path, dto.MediaTypeId, dto.Recursive, dto.IsEnabled), ct);
            return Ok(ApiResponse<ScanFolderDto>.Ok(ToDto(folder)));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<ScanFolderDto>.Fail("NOT_FOUND", ex.Message));
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        try
        {
            await _svc.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<ScanFolderDto>.Fail("NOT_FOUND", ex.Message));
        }
    }

    private static ScanFolderDto ToDto(Chronicle.Core.Models.ScanFolder f) =>
        new(f.Id, f.Path, f.MediaTypeId, f.MediaType?.DisplayName ?? "",
            f.Recursive, f.IsEnabled, f.CreatedAt, f.LastScannedAt);
}
