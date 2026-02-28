using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Chronicle.Services.Reports;

/// <summary>
/// Provides built-in analytical reports and delegates to loaded
/// <see cref="IReportPlugin"/> instances for custom reports.
///
/// Built-in report IDs are prefixed with "builtin:" to avoid collisions.
/// </summary>
public class ReportService : IReportService
{
    private const string DailyActivityId     = "builtin:daily-activity";
    private const string LibraryStatusId     = "builtin:library-status";
    private const string TopMediaId          = "builtin:top-media";
    private const string MonthlyWatchTimeId  = "builtin:monthly-watch-time";

    private static readonly IReadOnlyList<ReportDefinition> BuiltInDefinitions =
    [
        new(DailyActivityId,    "Daily Activity",
            "Scrobble count per day for the last 30 days",
            ChartType.Area),
        new(LibraryStatusId,    "Library Breakdown",
            "Items in your library grouped by status",
            ChartType.HorizontalBar),
        new(TopMediaId,         "Most Watched",
            "Top 20 most-scrobbled items for this user",
            ChartType.HorizontalBar),
        new(MonthlyWatchTimeId, "Monthly Watch Time",
            "Estimated watch time (hours) per month for the last 12 months",
            ChartType.Bar),
    ];

    private readonly ChronicleDbContext _db;
    private readonly IPluginRegistry    _registry;
    private readonly ILogger            _log = Log.ForContext<ReportService>();

    public ReportService(ChronicleDbContext db, IPluginRegistry registry)
    {
        _db       = db;
        _registry = registry;
    }

    // ── Catalogue ─────────────────────────────────────────────────────────────

    public IReadOnlyList<ReportDefinition> GetAllReports()
    {
        var all = new List<ReportDefinition>(BuiltInDefinitions);
        foreach (var plugin in _registry.GetReportPlugins())
            all.AddRange(plugin.GetReports());
        return all;
    }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    public async Task<ReportResult> RunReportAsync(
        string reportId,
        IReadOnlyDictionary<string, string> parameters,
        int userId,
        CancellationToken ct = default)
    {
        _log.Information("Running report {ReportId} for user {UserId}", reportId, userId);

        if (reportId.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
        {
            return reportId.ToLowerInvariant() switch
            {
                DailyActivityId    => await RunDailyActivityAsync(parameters, userId, ct),
                LibraryStatusId    => await RunLibraryStatusAsync(userId, ct),
                TopMediaId         => await RunTopMediaAsync(parameters, userId, ct),
                MonthlyWatchTimeId => await RunMonthlyWatchTimeAsync(userId, ct),
                _ => throw new InvalidOperationException($"Unknown built-in report: {reportId}")
            };
        }

        // Delegate to a loaded report plugin
        foreach (var plugin in _registry.GetReportPlugins())
        {
            if (plugin.GetReports().Any(r => r.ReportId == reportId))
                return await plugin.RunAsync(reportId, parameters, userId, ct);
        }

        throw new InvalidOperationException(
            $"Report '{reportId}' not found. " +
            "It may belong to a plugin that is not currently loaded.");
    }

    // ── Built-in: daily activity ───────────────────────────────────────────────

    private async Task<ReportResult> RunDailyActivityAsync(
        IReadOnlyDictionary<string, string> parameters,
        int userId,
        CancellationToken ct)
    {
        var days = parameters.TryGetValue("days", out var dStr) && int.TryParse(dStr, out var d) ? d : 30;
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.Date.AddDays(-days + 1);

        // Group by calendar date (SQLite stores as ISO string — EF handles it)
        var raw = await _db.InteractionEvents
            .Where(e => e.UserId == userId && e.Timestamp >= since)
            .GroupBy(e => e.Timestamp.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .OrderBy(g => g.Date)
            .ToListAsync(ct);

        // Ensure every day in range appears even if count is 0
        var lookup = raw.ToDictionary(r => r.Date, r => r.Count);
        var points = new List<ReportDataPoint>();
        for (var i = 0; i < days; i++)
        {
            var day = since.AddDays(i);
            var label = day.ToString("MMM d");
            points.Add(new ReportDataPoint(label, lookup.GetValueOrDefault(day, 0)));
        }

        var total   = points.Sum(p => p.Value);
        var weekAvg = days >= 7
            ? Math.Round(points.TakeLast(7).Sum(p => p.Value), 1)
            : total;

        return new ReportResult(
            DailyActivityId,
            $"Daily Activity — Last {days} Days",
            ChartType.Area,
            [new ReportSeries("Scrobbles", points)],
            [
                new ReportKpi("Total", total.ToString("N0")),
                new ReportKpi("Last 7 Days", weekAvg.ToString("N1")),
            ],
            DateTimeOffset.UtcNow);
    }

    // ── Built-in: library status breakdown ────────────────────────────────────

    private async Task<ReportResult> RunLibraryStatusAsync(int userId, CancellationToken ct)
    {
        var raw = await _db.UserLibraries
            .Where(l => l.UserId == userId)
            .GroupBy(l => l.Status)
            .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync(ct);

        var points = raw.Select(r => new ReportDataPoint(r.Status, r.Count)).ToList();
        var total  = points.Sum(p => p.Value);

        return new ReportResult(
            LibraryStatusId,
            "Library Breakdown by Status",
            ChartType.HorizontalBar,
            [new ReportSeries("Items", points)],
            [new ReportKpi("Total Library", total.ToString("N0"))],
            DateTimeOffset.UtcNow);
    }

    // ── Built-in: top media ────────────────────────────────────────────────────

    private async Task<ReportResult> RunTopMediaAsync(
        IReadOnlyDictionary<string, string> parameters,
        int userId,
        CancellationToken ct)
    {
        var limit = parameters.TryGetValue("limit", out var lStr) && int.TryParse(lStr, out var l) ? l : 20;
        limit = Math.Clamp(limit, 5, 100);

        var raw = await _db.InteractionEvents
            .Where(e => e.UserId == userId)
            .GroupBy(e => e.MediaItem!.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(limit)
            .ToListAsync(ct);

        var points = raw.Select(r => new ReportDataPoint(r.Name, r.Count)).ToList();

        return new ReportResult(
            TopMediaId,
            $"Top {limit} Most-Watched Items",
            ChartType.HorizontalBar,
            [new ReportSeries("Plays", points)],
            [],
            DateTimeOffset.UtcNow);
    }

    // ── Built-in: monthly watch time ──────────────────────────────────────────

    private async Task<ReportResult> RunMonthlyWatchTimeAsync(int userId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.Date.AddMonths(-11).AddDays(1 - DateTime.UtcNow.Day);

        // Estimate hours: RuntimeMinutes * ProgressPercent / 100 / 60
        var raw = await _db.InteractionEvents
            .Where(e => e.UserId == userId
                     && e.Timestamp >= since
                     && e.MediaItem!.RuntimeMinutes.HasValue)
            .Select(e => new
            {
                Month       = e.Timestamp.Month,
                Year        = e.Timestamp.Year,
                EstimatedH  = e.MediaItem!.RuntimeMinutes!.Value
                              * (e.ProgressPercent ?? 100.0) / 100.0 / 60.0,
            })
            .ToListAsync(ct);

        // Group client-side (SQLite date arithmetic through EF is unreliable for month grouping)
        var grouped = raw
            .GroupBy(r => new { r.Year, r.Month })
            .Select(g => new
            {
                Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM yy"),
                Hours = Math.Round(g.Sum(x => x.EstimatedH), 1),
                SortKey = g.Key.Year * 100 + g.Key.Month,
            })
            .OrderBy(g => g.SortKey)
            .ToList();

        var points     = grouped.Select(g => new ReportDataPoint(g.Label, g.Hours)).ToList();
        var totalHours = Math.Round(points.Sum(p => p.Value), 1);

        return new ReportResult(
            MonthlyWatchTimeId,
            "Monthly Watch Time (last 12 months)",
            ChartType.Bar,
            [new ReportSeries("Hours", points)],
            [new ReportKpi("Total (12 mo)", $"{totalHours:N1}h")],
            DateTimeOffset.UtcNow);
    }
}
