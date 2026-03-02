# Chronicle.Plugin.IFPI — Design Document

**Plugin ID:** `chronicle.plugin.ifpi`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (public charts — no API key required)
**API:** IFPI Global Charts — `https://www.ifpi.org/ifpi-global-charts`

---

## Purpose

[IFPI](https://www.ifpi.org/) (International Federation of the Phonographic
Industry) publishes the Global Single Chart and Global Album Chart — weekly
rankings aggregated from streaming and sales data across 20+ countries.
These are the only truly international music charts. This plugin enriches
Chronicle music entries with global commercial performance data, providing
a worldwide perspective that complements Billboard (US) and OfficialCharts (UK).

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 7 | IFPI Global Single Chart position |
| `album` | 7 | IFPI Global Album Chart position |
| `artist` | 8 | Global chart history |

---

## Data Available (scraped)

| Page | Chart |
|------|-------|
| `https://www.ifpi.org/ifpi-global-charts/` | Global Single Chart (Top 20) |
| `https://www.ifpi.org/ifpi-global-album-chart/` | Global Album Chart (Top 10) |

IFPI chart pages embed chart data in the page HTML. The charts cover
the top 10–20 globally streamed/sold tracks and albums per week.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 2000 |

---

## Fields Populated

```
title, cast (artists),
metadata_json: { ifpi_chart_positions: [{
                   chart_name, current_position,
                   peak_position, weeks_on_chart,
                   chart_date
                 }] }
```

---

## Rate Limits

- Small public site; minimum 2,000 ms between requests
- Cache chart data for 7 days (IFPI charts update weekly)

---

## Implementation Notes

- IFPI Global Charts cover only the top 10–20 globally, so matches will
  be infrequent — this plugin is most valuable for identifying
  international megahits
- The Global Single Chart aggregates streaming data from 20+ countries
  weighted by market size
- IFPI also publishes annual Global Music Report data (streaming totals,
  regional breakdowns) — a future enhancement could harvest annual stats
- Low priority (7/8) as chart coverage is narrow (top 10–20 only)

---

## Scaffold Location

```
Chronicle.Plugin.IFPI/
├── Chronicle.Plugin.IFPI.csproj
├── README.md
├── manifest.json
├── IFPIPlugin.cs
└── Models/
    └── IfpiChartEntry.cs
```
