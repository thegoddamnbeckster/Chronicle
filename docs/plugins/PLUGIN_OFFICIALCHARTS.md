# Chronicle.Plugin.OfficialCharts — Design Document

**Plugin ID:** `chronicle.plugin.officialcharts`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (web scraping — no public API)
**API:** Official Charts Company — `https://www.officialcharts.com`

---

## Purpose

[The Official Charts Company](https://www.officialcharts.com/) is the
authoritative source for UK music chart rankings, operating the Official
UK Singles Chart, Albums Chart, and 40+ genre charts. UK chart performance
is a significant metric for British and international artists. This plugin
provides UK-specific chart metadata to complement Billboard's US focus.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 6 | UK Singles Chart positions |
| `album` | 6 | UK Albums Chart positions |
| `artist` | 7 | UK chart history |

---

## Data Available (scraped)

| Chart URL | Chart Name |
|-----------|-----------|
| `/charts/singles-chart/` | Official UK Singles Chart (Top 100) |
| `/charts/albums-chart/` | Official UK Albums Chart (Top 100) |
| `/charts/dance-singles-chart/` | Official UK Dance Singles Chart |
| `/charts/r-and-b-singles-chart/` | Official UK R&B Chart |
| `/charts/rock-singles-chart/` | Official UK Rock Chart |

Chart pages include structured data with current position, peak, and
weeks on chart.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `charts` | Charts to Fetch | MultiSelect | No | Default: `singles-chart,albums-chart` |
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 2000 |

---

## Fields Populated

```
title, cast (artists),
metadata_json: { uk_chart_positions: [{
                   chart_name, current_position,
                   peak_position, weeks_on_chart,
                   debut_date, chart_date
                 }] }
```

---

## Rate Limits

- Minimum 2,000 ms between requests with jitter
- Cache chart data for 7 days (charts update weekly, usually Friday)

---

## Implementation Notes

- The Official Charts updates on Fridays — schedule refreshes
  accordingly if chart data is being tracked over time
- Chart page JSON is embedded in `<script type="application/json">`
  tags — parse these for structured data
- Historical chart data is available at `/charts/{slug}/{date}/`
  where date is in `YYYYMMDD` format
- `peak_position` and `weeks_on_chart` are the primary data points
  of interest for Chronicle

---

## Scaffold Location

```
Chronicle.Plugin.OfficialCharts/
├── Chronicle.Plugin.OfficialCharts.csproj
├── README.md
├── manifest.json
├── OfficialChartsPlugin.cs
└── Models/
    ├── OfficialChart.cs
    └── OfficialChartEntry.cs
```
