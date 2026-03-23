using System.Text.Json;
using Chronicle.API.DTOs;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/enrichment")]
[Authorize]
public class EnrichmentController(
    IMetadataEnrichmentService enrichmentSvc,
    IServiceScopeFactory scopeFactory) : ControllerBase
{
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var stats = await enrichmentSvc.GetStatsAsync(ct);
        var dtos = stats.Select(s => new EnrichmentStatsDto(
            s.PluginId, s.PluginName, s.Pending, s.Completed, s.Failed, s.Exhausted, s.NotFound, s.Skipped));
        return Ok(new { success = true, data = dtos });
    }

    [HttpPost("{pluginId}/run")]
    [Authorize(Roles = "Admin")]
    public IActionResult RunEnrichment(string pluginId)
    {
        _ = Task.Run(async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<IMetadataEnrichmentService>();
            await svc.EnrichPendingAsync(pluginId, CancellationToken.None);
        });
        return Accepted(new { success = true, message = $"Enrichment started for {pluginId}" });
    }

    [HttpPost("{pluginId}/reset")]
    [Authorize(Roles = "Admin")]
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
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Skip(string pluginId, int mediaItemId, CancellationToken ct)
    {
        await enrichmentSvc.SkipAsync(mediaItemId, pluginId, ct);
        return Ok(new { success = true });
    }

    [HttpGet("{pluginId}/items")]
    public async Task<IActionResult> GetItems(
        string pluginId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 200) pageSize = 50;

        var result = await enrichmentSvc.GetItemsAsync(pluginId, status, page, pageSize, search, ct);

        var items = result.Items.Select(r =>
        {
            JsonElement? diag = null;
            if (r.DiagnosticsJson is not null)
            {
                try { diag = JsonSerializer.Deserialize<JsonElement>(r.DiagnosticsJson); }
                catch { /* ignore */ }
            }

            JsonElement? scanner = null;
            if (r.FileScannerMetadataJson is not null)
            {
                try { scanner = JsonSerializer.Deserialize<JsonElement>(r.FileScannerMetadataJson); }
                catch { /* ignore */ }
            }

            return new
            {
                enrichmentId        = r.EnrichmentId,
                mediaItemId         = r.MediaItemId,
                name                = r.Name,
                year                = r.Year,
                mediaType           = r.MediaType,
                hierarchyLevel      = r.HierarchyLevel,
                posterUrl           = r.PosterUrl,
                externalId          = r.ExternalId,
                status              = r.Status.ToString(),
                errorMessage        = r.ErrorMessage,
                retryCount          = r.RetryCount,
                maxRetries          = r.MaxRetries,
                lastAttemptedAt     = r.LastAttemptedAt,
                diagnostics         = diag,
                fileScannerMetadata = scanner,
            };
        });

        return Ok(new
        {
            success = true,
            data = new
            {
                items,
                total      = result.Total,
                page       = result.Page,
                pageSize   = result.PageSize,
                totalPages = result.PageSize > 0
                    ? (int)Math.Ceiling(result.Total / (double)result.PageSize)
                    : 1,
            }
        });
    }
}
