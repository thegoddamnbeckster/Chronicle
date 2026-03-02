# Chronicle.Plugin.Traxsource — Design Document

**Plugin ID:** `chronicle.plugin.traxsource`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (public API, unofficial)
**API:** Traxsource — `https://www.traxsource.com` (scraping + JSON endpoints)

---

## Purpose

[Traxsource](https://www.traxsource.com/) is a major electronic music
download store focused on house, techno, and underground dance music.
It competes with Beatport and often has superior coverage for deep house,
afro house, and Latin-influenced electronic genres. Traxsource exposes
JSON data through its page-embedded API that can be used without
authentication for metadata purposes.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 3 | EDM track metadata (house/techno focus) |
| `album` | 3 | Release detail |
| `artist` | 3 | DJ/producer profile |

---

## API Overview

Traxsource embeds JSON data at predictable endpoints:

| URL Pattern | Data |
|-------------|------|
| `traxsource.com/title/{id}/{slug}` | Track page with embedded JSON |
| `traxsource.com/release/{id}/{slug}` | Release page |
| `traxsource.com/artist/{id}/{slug}` | Artist page |
| `api.traxsource.com/api/v1/track?ids={id}` | Track JSON (unofficial) |
| `api.traxsource.com/api/v1/release?ids={id}` | Release JSON |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 1500 |
| `include_chart_data` | Include Chart Rankings | Boolean | No | Default: true |

---

## Fields Populated

```
title, year, genres, cast (artists), poster_url, duration,
traxsource_id, traxsource_url,
metadata_json: { bpm, key, label_name, release_date,
                 mix_name, sub_genre, isrc, chart_rank }
```

---

## Rate Limits

- No official rate limit; minimum 1,500 ms between requests
- Cache track/release data for 7 days

---

## Implementation Notes

- Traxsource's primary value over Beatport is its **deep house and
  underground electronic** catalogue — use it as a fallback when
  Beatport does not have a match
- The unofficial JSON API (`api.traxsource.com`) returns cleaner data
  than scraping the HTML pages — prefer this approach
- Track IDs are stable integers in the URL path
- Genre taxonomy overlaps significantly with Beatport — map to the same
  Chronicle genre set; store raw Traxsource sub-genre in metadata
- `key` is returned in both standard notation and Camelot format
- Traxsource "Soulful" / "Deep" / "Afro" sub-genre labels are useful
  for niche EDM classification

---

## Scaffold Location

```
Chronicle.Plugin.Traxsource/
├── Chronicle.Plugin.Traxsource.csproj
├── README.md
├── manifest.json
├── TraxsourcePlugin.cs
└── Models/
    ├── TraxsourceTrack.cs
    └── TraxsourceRelease.cs
```
