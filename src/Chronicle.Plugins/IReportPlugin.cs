namespace Chronicle.Plugins;

// ── Chart types ───────────────────────────────────────────────────────────────

public enum ChartType
{
    /// <summary>Area chart — for time-series data.</summary>
    Area,
    /// <summary>Vertical bar chart — for comparisons between periods.</summary>
    Bar,
    /// <summary>Horizontal bar chart — for ranked lists.</summary>
    HorizontalBar,
    /// <summary>Pie / donut chart — for proportional breakdowns.</summary>
    Pie,
    /// <summary>Plain data table — for raw listing.</summary>
    Table,
}

// ── Report data structures ────────────────────────────────────────────────────

/// <summary>A single (label, value) point in a report series.</summary>
public record ReportDataPoint(string Label, double Value);

/// <summary>One named data series containing an ordered list of data points.</summary>
public record ReportSeries(string Name, IReadOnlyList<ReportDataPoint> Points);

/// <summary>A key performance indicator — a single headline metric.</summary>
public record ReportKpi(string Label, string Value, string? Trend = null);

/// <summary>Describes a report available from a plugin (shown in the report picker UI).</summary>
public record ReportDefinition(
    string   ReportId,
    string   Name,
    string   Description,
    ChartType DefaultChartType
);

/// <summary>
/// The complete result of running a report.
/// Contains one or more <see cref="Series"/> for charting plus optional
/// <see cref="Kpis"/> for headline numbers.
/// </summary>
public record ReportResult(
    string                    ReportId,
    string                    Title,
    ChartType                 ChartType,
    IReadOnlyList<ReportSeries> Series,
    IReadOnlyList<ReportKpi>  Kpis,
    DateTimeOffset            GeneratedAt
);

// ── Interface ─────────────────────────────────────────────────────────────────

/// <summary>
/// A plugin that provides one or more analytical reports over Chronicle data.
///
/// Built-in reports are implemented in Chronicle.Services and loaded alongside
/// the application. Third-party report plugins can be loaded as DLLs and will
/// be discovered automatically.
///
/// Parameters are passed as a dictionary — consult each report's
/// <see cref="ReportDefinition"/> for supported keys.
/// </summary>
public interface IReportPlugin
{
    string PluginId    { get; }
    string Name        { get; }
    string Version     { get; }
    string Description { get; }

    /// <summary>Returns the list of reports this plugin provides.</summary>
    IReadOnlyList<ReportDefinition> GetReports();

    /// <summary>
    /// Executes the named report and returns the result.
    /// </summary>
    /// <param name="reportId">Matches one of the IDs returned by <see cref="GetReports"/>.</param>
    /// <param name="parameters">Optional parameters (e.g. date range, limit).</param>
    /// <param name="userId">The Chronicle user whose data to analyse.</param>
    Task<ReportResult> RunAsync(
        string reportId,
        IReadOnlyDictionary<string, string> parameters,
        int userId,
        CancellationToken ct = default
    );
}
