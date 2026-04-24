using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/sync")]
[Authorize]
public class SyncController : ControllerBase
{
    private readonly ISyncOrchestrationService _sync;

    public SyncController(ISyncOrchestrationService sync)
    {
        _sync = sync;
    }

    /// <summary>Manually trigger an import-all or delta-sync for a plugin.</summary>
    [HttpPost("{pluginId}")]
    public async Task<IActionResult> TriggerSync(
        string pluginId,
        [FromQuery] bool fullSync = false,
        CancellationToken ct = default)
    {
        var userId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id : (int?)null;
        try
        {
            var summary = await _sync.SyncAsync(pluginId, fullSync, userId, ct);
            return Ok(ApiResponse<SyncSummary>.Ok(summary));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not authenticated"))
        {
            return UnprocessableEntity(ApiResponse<object>.Fail("NOT_AUTHENTICATED", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", ex.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiResponse<object>.Fail("SYNC_FAILED", ex.Message));
        }
    }
}
