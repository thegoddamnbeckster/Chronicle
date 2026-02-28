using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Plugins;
using Chronicle.Services.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

/// <summary>
/// Provides analytical reports derived from the user's Chronicle data.
///
/// Built-in reports are always available. Additional reports may be provided
/// by loaded <see cref="IReportPlugin"/> assemblies.
///
/// GET /api/v1/reports      — list available reports
/// GET /api/v1/reports/run  — run a report (?reportId=builtin:daily-activity&amp;days=30)
/// </summary>
[ApiController]
[Route("api/v1/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly IReportService _reportService;

    public ReportsController(IReportService reportService)
    {
        _reportService = reportService;
    }

    // ── GET /api/v1/reports ───────────────────────────────────────────────────

    /// <summary>
    /// Lists all available reports (built-in and plugin-provided).
    /// The response includes the report's ID, name, description, and default chart type.
    /// </summary>
    [HttpGet]
    public IActionResult GetReports()
    {
        var reports = _reportService.GetAllReports();
        var dtos = reports.Select(r => new
        {
            r.ReportId,
            r.Name,
            r.Description,
            DefaultChartType = r.DefaultChartType.ToString(),
        });
        return Ok(ApiResponse<object>.Ok(dtos));
    }

    // ── GET /api/v1/reports/{id}/run ─────────────────────────────────────────

    /// <summary>
    /// Runs the specified report for the authenticated user.
    /// Any query-string parameters (besides reportId) are forwarded to the report.
    /// Example: GET /api/v1/reports/run?reportId=builtin:daily-activity&amp;days=30
    /// </summary>
    [HttpGet("run")]
    public async Task<IActionResult> RunReport([FromQuery] string reportId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reportId))
            return BadRequest(ApiResponse<ReportResultDto>.Fail("BAD_REQUEST", "reportId is required."));

        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // Forward all query params except the routing 'reportId' as report parameters
        var parameters = Request.Query
            .Where(q => q.Key != "reportId" && !string.IsNullOrWhiteSpace(q.Value))
            .ToDictionary(q => q.Key, q => q.Value.ToString());

        try
        {
            var result = await _reportService.RunReportAsync(reportId, parameters, userId, ct);
            return Ok(ApiResponse<ReportResultDto>.Ok(ToDto(result)));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<ReportResultDto>.Fail("REPORT_NOT_FOUND", ex.Message));
        }
    }

    // ── DTO mapping ───────────────────────────────────────────────────────────

    private static ReportResultDto ToDto(ReportResult r) => new(
        r.ReportId,
        r.Title,
        r.ChartType.ToString(),
        r.Series.Select(s => new ReportSeriesDto(
            s.Name,
            s.Points.Select(p => new ReportDataPointDto(p.Label, p.Value)).ToList()
        )).ToList(),
        r.Kpis.Select(k => new ReportKpiDto(k.Label, k.Value, k.Trend)).ToList(),
        r.GeneratedAt
    );
}

// ── Response DTOs ─────────────────────────────────────────────────────────────

public record ReportDataPointDto(string Label, double Value);
public record ReportSeriesDto(string Name, List<ReportDataPointDto> Points);
public record ReportKpiDto(string Label, string Value, string? Trend);
public record ReportResultDto(
    string ReportId,
    string Title,
    string ChartType,
    List<ReportSeriesDto> Series,
    List<ReportKpiDto> Kpis,
    DateTimeOffset GeneratedAt
);
