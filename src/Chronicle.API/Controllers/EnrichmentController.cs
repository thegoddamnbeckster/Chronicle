using Chronicle.API.DTOs;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/enrichment")]
[Authorize]
public class EnrichmentController(IMetadataEnrichmentService enrichmentSvc) : ControllerBase
{
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var stats = await enrichmentSvc.GetStatsAsync(ct);
        var dtos = stats.Select(s => new EnrichmentStatsDto(
            s.PluginId, s.Pending, s.Completed, s.Failed, s.Exhausted, s.NotFound, s.Skipped));
        return Ok(new { success = true, data = dtos });
    }

    [HttpPost("{pluginId}/run")]
    public IActionResult RunEnrichment(string pluginId)
    {
        _ = Task.Run(() => enrichmentSvc.EnrichPendingAsync(pluginId, CancellationToken.None));
        return Accepted(new { success = true, message = $"Enrichment started for {pluginId}" });
    }

    [HttpPost("{pluginId}/reset")]
    public async Task<IActionResult> Reset(string pluginId, [FromBody] ResetEnrichmentDto dto, CancellationToken ct)
    {
        ResetScope scope;
        switch (dto.Scope.ToLower())
        {
            case "single":    scope = ResetScope.Single; break;
            case "exhausted": scope = ResetScope.AllExhausted; break;
            case "all":       scope = ResetScope.AllForPlugin; break;
            default:
                return BadRequest(new { success = false, error = new { code = "INVALID_SCOPE", message = $"Invalid scope '{dto.Scope}'. Valid values: single, exhausted, all." } });
        }
        await enrichmentSvc.ResetAsync(pluginId, scope, dto.MediaItemId, ct);
        return Ok(new { success = true });
    }

    [HttpPost("{pluginId}/items/{mediaItemId:int}/skip")]
    public async Task<IActionResult> Skip(string pluginId, int mediaItemId, CancellationToken ct)
    {
        await enrichmentSvc.SkipAsync(mediaItemId, pluginId, ct);
        return Ok(new { success = true });
    }
}
