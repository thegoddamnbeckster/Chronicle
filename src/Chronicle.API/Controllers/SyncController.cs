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
    private readonly ISyncJobTracker _jobs;

    public SyncController(ISyncOrchestrationService sync, ISyncJobTracker jobs)
    {
        _sync = sync;
        _jobs = jobs;
    }

    /// <summary>
    /// Starts an import-all or delta-sync for a plugin in the background.
    /// Returns 202 Accepted immediately with a jobId.
    /// Poll GET ./{pluginId}/job/{jobId} until status is "complete" or "failed".
    /// </summary>
    [HttpPost("{pluginId}")]
    public IActionResult TriggerSync(
        string pluginId,
        [FromQuery] bool fullSync = false)
    {
        var userId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id : (int?)null;

        var jobId = _jobs.Enqueue(
            () => _sync.SyncAsync(pluginId, fullSync, userId, CancellationToken.None));

        return Accepted(ApiResponse<object>.Ok(new { jobId }));
    }

    /// <summary>
    /// Polls the status of a background sync job started via POST.
    /// Status is one of: "running" | "complete" | "failed".
    /// </summary>
    [HttpGet("{pluginId}/job/{jobId}")]
    public IActionResult GetJobStatus(string pluginId, string jobId)
    {
        var snap = _jobs.GetSnapshot(jobId);
        if (snap is null)
            return NotFound(ApiResponse<object>.Fail("JOB_NOT_FOUND", $"Job '{jobId}' not found."));

        return Ok(ApiResponse<SyncJobSnapshot>.Ok(snap));
    }
}
