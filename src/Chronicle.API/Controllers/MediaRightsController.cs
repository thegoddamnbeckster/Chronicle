using Chronicle.API.DTOs;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

public sealed record MediaRightsDto(bool DeleteAdminOnly);
public sealed record SetMediaRightsRequest(bool DeleteAdminOnly);

/// <summary>Who may delete shared media. Anyone signed in can read it (the UI hides buttons accordingly); only an administrator changes it.</summary>
[ApiController]
[Route("api/v1/media-rights")]
[Authorize]
public sealed class MediaRightsController(IMediaRightsPolicy policy) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(ApiResponse<MediaRightsDto>.Ok(new MediaRightsDto(await policy.IsDeleteAdminOnlyAsync(ct))));

    [HttpPut]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Set([FromBody] SetMediaRightsRequest request, CancellationToken ct)
    {
        await policy.SetDeleteAdminOnlyAsync(request.DeleteAdminOnly, ct);
        return Ok(ApiResponse<MediaRightsDto>.Ok(new MediaRightsDto(request.DeleteAdminOnly)));
    }
}
