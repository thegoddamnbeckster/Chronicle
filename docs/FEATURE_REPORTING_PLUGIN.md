# Feature Design: Reporting & Analytics Plugin System

**Status:** Design/Planning
**Target:** Phase 3
**Goal:** Provide a flexible reporting framework that ships with built-in charts/stats and lets users (and plugin authors) define custom reports using Chronicle's data. Reports are rendered as interactive charts in the frontend.

---

## Overview

The reporting system has three tiers:

| Tier | Description | Author |
|---|---|---|
| **Built-in reports** | Pre-defined stats (watch time, top genres, listening history, etc.) | Chronicle team |
| **User custom reports** | SQL-like query builder with drag-and-drop chart configuration | End users |
| **Plugin reports** | `IReportPlugin` implementations that run arbitrary C# against Chronicle's DbContext | Plugin developers |

All three tiers produce the same output format — a `ReportResult` — which the frontend renders using a chart library (Recharts or Chart.js).

---

## IReportPlugin Interface

```csharp
// Chronicle.Plugins/IReportPlugin.cs

public interface IReportPlugin
{
    string PluginId    { get; }
    string Name        { get; }
    string Description { get; }
    string Version     { get; }
    string Author      { get; }

    /// <summary>Category shown in the Reports sidebar (e.g. "Movies", "Music", "Custom").</summary>
    string Category { get; }

    /// <summary>
    /// Returns the schema of configurable parameters for this report.
    /// Examples: date range, user ID, media type filter, top-N count.
    /// </summary>
    IReadOnlyList<ReportParameter> GetParameters();

    /// <summary>
    /// Executes the report and returns the result.
    /// Chronicle injects a read-only DbContext to prevent mutations.
    /// </summary>
    Task<ReportResult> RunAsync(
        IReadOnlyDictionary<string, string> parameters,
        IQueryable<InteractionEvent> events,
        IQueryable<UserLibrary> library,
        IQueryable<MediaItem> media,
        CancellationToken ct = default);
}
```

### ReportParameter — declare inputs

```csharp
public record ReportParameter(
    string   Key,
    string   Label,
    string   Type,          // "date" | "daterange" | "number" | "select" | "text" | "userid"
    string?  DefaultValue,
    bool     Required,
    IReadOnlyList<SelectOption>? Options = null
);
```

### ReportResult — standardised output

```csharp
public record ReportResult(
    /// <summary>Chart type hint for the frontend.</summary>
    ChartType     ChartType,
    /// <summary>Series labels (X axis categories or pie segment names).</summary>
    List<string>  Labels,
    /// <summary>One or more data series.</summary>
    List<ReportSeries> Series,
    /// <summary>Optional summary KPIs shown above the chart.</summary>
    List<ReportKpi>?   Kpis = null,
    /// <summary>Optional raw tabular data for the "Table" view.</summary>
    List<Dictionary<string, object?>>? TableRows = null
);

public record ReportSeries(string Name, List<double> Values, string? Color = null);

public record ReportKpi(string Label, string Value, string? Delta = null, string? DeltaDirection = null);

public enum ChartType { Bar, Line, Area, Pie, Donut, Scatter, Heatmap, Table }
```

---

## Built-in Reports

### Watch / Listen statistics

| Report | Chart | Description |
|---|---|---|
| Watch Time by Week | Area | Hours watched per week (rolling 52 weeks) |
| Watch Time by Month | Bar | Hours watched per month (rolling 24 months) |
| Watch Time by Day of Week | Bar | Which days the user watches most |
| Watch Time by Hour of Day | Bar (heatmap) | Time-of-day usage pattern |
| Listening Time This Week | KPI + Line | Music listening (minutes) per day this week |
| Total Watch Time | KPI | All-time hours + this year vs last year |

### Library statistics

| Report | Chart | Description |
|---|---|---|
| Library Size Over Time | Area | Cumulative items added to library by month |
| Status Breakdown | Donut | % of library per status (Watching, Completed, etc.) |
| Media Type Breakdown | Donut | Movies vs TV vs Music vs Anime |
| Completion Rate | KPI | % of library marked Completed |
| Items Added Per Month | Bar | New library entries per month |

### Per-item statistics

| Report | Chart | Description |
|---|---|---|
| Most Watched Items | Bar (horizontal) | Top N items by watch count (all time) |
| Most Watched This Month | Table | Top 10 by interaction events this month |
| Rewatch Stats | KPI | Items watched more than once + average rewatch count |
| Play Count for Specific Item | Line | How many times `?mediaId=X` was played per month |

### Rating statistics

| Report | Chart | Description |
|---|---|---|
| Rating Distribution | Bar | Count of items rated 1–10 |
| Average Rating by Media Type | Bar | Avg rating: movies vs TV vs music |
| Ratings Over Time | Line | Average rating of items rated per month |

### User statistics (admin only)

| Report | Chart | Description |
|---|---|---|
| Active Users | Line | DAU/WAU/MAU over time |
| Most Active Users | Table | Top users by watch events |
| Scrobble Volume | Bar | Total scrobble events per day |

---

## Custom Report Builder (User-Defined)

Users can build reports without writing code:

### Query builder

Exposes a simplified "query" model:

```json
{
  "metric": "watch_time",        // watch_time | item_count | rating_avg | event_count
  "groupBy": "week",             // day | week | month | year | media_type | genre | none
  "filters": {
    "mediaType": "movie",
    "dateRange": { "from": "2025-01-01", "to": "2025-12-31" },
    "userId": 1
  },
  "chartType": "Line",
  "topN": null                   // if set, return only top N items
}
```

Chronicle translates this to an EF Core LINQ query server-side (never raw SQL from user input, preventing injection). The query builder UI is a form-based interface in Settings → Reports → New Report.

### Saved reports

Users can:
- Name and save custom report configurations
- Add saved reports to their dashboard as widgets
- Share a report config as a JSON export

---

## API

```
GET  /api/v1/reports                       List available reports (built-in + plugin)
GET  /api/v1/reports/{reportId}            Get report metadata and parameter schema
POST /api/v1/reports/{reportId}/run        Execute a report with parameters → ReportResult
GET  /api/v1/reports/custom                List user's saved custom reports
POST /api/v1/reports/custom                Save a new custom report
PUT  /api/v1/reports/custom/{id}           Update a custom report
DELETE /api/v1/reports/custom/{id}         Delete a custom report
POST /api/v1/reports/custom/{id}/run       Run a saved custom report with optional param overrides
```

Request body for `POST .../run`:
```json
{
  "parameters": {
    "dateFrom": "2025-01-01",
    "dateTo":   "2025-12-31",
    "userId":   1,
    "topN":     "10"
  }
}
```

---

## Plugin Report Example

```csharp
// In a Chronicle plugin DLL:
public class MostBingedShowsReport : IReportPlugin
{
    public string PluginId    => "report-most-binged-shows";
    public string Name        => "Most Binged Shows";
    public string Description => "TV shows with the most episodes watched in a single day";
    public string Version     => "1.0.0";
    public string Author      => "Example Author";
    public string Category    => "TV";

    public IReadOnlyList<ReportParameter> GetParameters() =>
    [
        new ReportParameter("topN",     "Top N shows", "number", "10", false),
        new ReportParameter("dateFrom", "From date",   "date",   null,  false),
        new ReportParameter("dateTo",   "To date",     "date",   null,  false),
    ];

    public async Task<ReportResult> RunAsync(
        IReadOnlyDictionary<string, string> parameters,
        IQueryable<InteractionEvent> events,
        IQueryable<UserLibrary> library,
        IQueryable<MediaItem> media,
        CancellationToken ct)
    {
        var topN = int.TryParse(parameters.GetValueOrDefault("topN"), out var n) ? n : 10;

        // Find shows with episodes watched in a binge session (>=3 eps same day)
        var binges = await events
            .Where(e => e.MediaItem.MediaType.Name == "tv")
            .GroupBy(e => new { e.MediaItem.ParentId, Date = e.Timestamp.Date })
            .Where(g => g.Count() >= 3)
            .GroupBy(g => g.Key.ParentId)
            .Select(g => new { ShowId = g.Key, BingeDays = g.Count() })
            .OrderByDescending(x => x.BingeDays)
            .Take(topN)
            .ToListAsync(ct);

        // ... map to ReportResult
    }
}
```

---

## Frontend Integration

### Dashboard widgets

Saved reports can be pinned to the dashboard as chart widgets. The `IWidgetPlugin` interface is extended to support report-backed widgets:
- Widget settings include `reportId` + serialised `parameters`
- Widget `RenderAsync` delegates to the report engine

### Chart rendering

Uses **Recharts** (already chosen for the React frontend):
- `ChartType.Bar` → `<BarChart>`
- `ChartType.Line` / `Area` → `<LineChart>` / `<AreaChart>`
- `ChartType.Pie` / `Donut` → `<PieChart>`
- `ChartType.Table` → `<DataTable>` (custom sortable table component)

### Reports page

```
/reports                     ← all available reports
/reports/{reportId}          ← run a specific report, parameter form + chart
/reports/custom/new          ← custom report query builder
/reports/custom/{id}         ← saved custom report
```

---

## Performance Considerations

- Report queries run against the primary database (no separate reporting database)
- All reports are executed asynchronously with a 30-second timeout
- Results are cached in memory for 5 minutes (configurable per report via `[ReportCacheDuration(minutes: 60)]` attribute)
- Large date ranges are automatically chunked server-side
- Heavy reports (e.g. full library stats) are pre-computed nightly by a background `ReportCacheWarmupService`

---

## Implementation Order

1. Add `IReportPlugin` + `ReportResult`/`ReportSeries`/`ReportKpi`/`ReportParameter` to `Chronicle.Plugins`
2. Extend `IPluginRegistry` to discover `IReportPlugin` instances
3. Implement `ReportService` / `IReportService` with built-in reports
4. Add `ReportsController` REST endpoints
5. Implement custom query builder translation to LINQ
6. Add `SavedReport` EF model + migration
7. Frontend: Reports page, chart components, custom report builder UI
8. Frontend: Dashboard widget integration
