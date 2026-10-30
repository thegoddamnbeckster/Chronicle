using System.ComponentModel.DataAnnotations;
using Chronicle.API.Authentication;
using Chronicle.API.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

/// <summary>
/// Settings -> Watchdog: switch the watchdog (the small windowless helper that restarts Chronicle if it stops
/// answering) on or off, and see what it has been doing. Administrators with a browser session only.
/// </summary>
[ApiController]
[Route("api/v1/settings/watchdog")]
[Authorize(Policy = AuthPolicies.SessionAdmin)]
public class WatchdogController : ControllerBase
{
    private readonly WatchdogService _watchdog;

    public WatchdogController(WatchdogService watchdog) => _watchdog = watchdog;

    public sealed record WatchdogRequest([Required] bool Enabled);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(ApiResponse<WatchdogStatusDto>.Ok(await _watchdog.GetStatusAsync(ct)));

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] WatchdogRequest req, CancellationToken ct)
    {
        try
        {
            await _watchdog.SetEnabledAsync(req.Enabled, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("WATCHDOG_UNSUPPORTED", ex.Message));
        }
        return Ok(ApiResponse<WatchdogStatusDto>.Ok(await _watchdog.GetStatusAsync(ct)));
    }
}
