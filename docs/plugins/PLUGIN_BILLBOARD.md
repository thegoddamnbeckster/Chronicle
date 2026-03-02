# Chronicle.Plugin.Billboard — Design Document

**Plugin ID:** `chronicle.plugin.billboard`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (web scraping — no public API)
**API:** Billboard — `https://www.billboard.com/charts`

---

## Purpose

[Billboard](https://www.billboard.com/) is the definitive source for music
chart rankings in the United States, publishing the Hot 100 (singles),
Billboard 200 (albums), and dozens of genre-specific charts. This plugin
enriches Chronicle music entries with current and historical chart positions
— peak position, weeks on chart, and chart debut date — providing
quantitative commercial performance metadata.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 6 | Hot 100 and genre chart positions |
| `album` | 6 | Billboard 200 chart positions |
| `artist` | 7 | Artist chart history |

---

## Data Available (scraped)

| Chart URL | Chart Name |
|-----------|-----------|
| `/charts/hot-100/` | Billboard Hot 100 (singles) |
| `/charts/billboard-200/` | Billboard 200 (albums) |
| `/charts/artist-100/` | Artist 100 |
| `/charts/hot-country-songs/` | Hot Country Songs |
| `/charts/hot-r-and-b-hip-hop-songs/` | Hot R&B/Hip-Hop Songs |
| `/charts/hot-rock-songs/` | Hot Rock Songs |
| `/charts/dance-club-songs/` | Dance Club Songs |

Chart pages embed JSON-LD and `window.__PRELOADED_STATE__` JS object with
full chart data including `peakPosition`, `weeksOnChart`, `lastPosition`.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `charts` | Charts to Fetch | MultiSelect | No | Default: `hot-100,billboard-200` |
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 2000 |
| `include_history` | Fetch Chart History | Boolean | No | Default: false |

---

## Fields Populated

```
title, cast (artists),
metadata_json: { chart_positions: [{
                   chart_name, chart_slug, current_position,
                   peak_position, weeks_on_chart,
                   debut_date, last_position
                 }] }
```

---

## Rate Limits

- Billboard actively protects chart data; minimum 2,000 ms between requests
- Implement random jitter and rotate User-Agent strings
- Cache chart data for 7 days (charts update weekly)

---

## Implementation Notes

- Billboard chart data is **dynamic** — positions change weekly
  Cache only for 7 days (chart update cycle)
- `window.__PRELOADED_STATE__` in the page source contains the full
  chart JSON — parse this rather than scraping HTML table rows
- Multiple chart positions can be stored per track in the JSON array
- Historical chart data (per-week positions) requires fetching
  `/charts/{slug}/{date}/` — implement only if `include_history` is true
- Chart entry date and peak position are the most valuable data points
  for Chronicle's media enrichment

---

## Scaffold Location

```
Chronicle.Plugin.Billboard/
├── Chronicle.Plugin.Billboard.csproj
├── README.md
├── manifest.json
├── BillboardPlugin.cs
└── Models/
    ├── BillboardChart.cs
    └── BillboardEntry.cs
```
