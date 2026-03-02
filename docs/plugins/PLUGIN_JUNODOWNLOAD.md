# Chronicle.Plugin.JunoDownload — Design Document

**Plugin ID:** `chronicle.plugin.junodownload`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (web scraping — no public API)
**API:** Juno Download — `https://www.junodownload.com`

---

## Purpose

[Juno Download](https://www.junodownload.com/) is a long-established
electronic music specialist retailer (since 1996) covering a uniquely
broad range of electronic sub-genres including drum & bass, jungle,
breakbeat, techno, grime, and experimental. Its catalogue depth in
older/rarer electronic releases surpasses Beatport and Traxsource.
This plugin scrapes Juno Download for EDM metadata with a focus on
niche and historical electronic releases.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 4 | Niche/historical EDM track metadata |
| `album` | 4 | Release detail |
| `artist` | 4 | DJ/producer profile |

---

## Data Available (scraped)

| Page | Data Extracted |
|------|---------------|
| `/products/detail/{id}/{slug}/` | Title, artists, label, BPM, key, genres, cover |
| `/artists/{slug}/` | Artist profile and releases |
| Search (`/search/?q=`) | Search results with release IDs |

Juno Download pages include `application/ld+json` schema markup:
```json
{
  "@type": "Product",
  "name": "...",
  "brand": { "name": "..." },
  "offers": { "price": "..." }
}
```

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 2000 |
| `include_tracklist` | Fetch Tracklist | Boolean | No | Default: true |

---

## Fields Populated

```
title, year, genres, cast (artists), poster_url,
junodownload_id, junodownload_url,
metadata_json: { bpm, key, label_name, catalog_number,
                 format, sub_genre, tracklist }
```

---

## Rate Limits

- No official rate limit; minimum 2,000 ms between requests with jitter
- Cache release data for 14 days

---

## Implementation Notes

- Juno Download's primary value is historical and niche EDM coverage —
  use it as a tertiary fallback after Beatport and Traxsource
- Release IDs are embedded in the product URL path as integers
- `format` field (Vinyl / Digital) is important for record-collector
  use cases — store in `metadata_json.format`
- Catalog number (label catalogue reference) is displayed on release
  pages — valuable for physical media identification
- BPM and key are shown in the release detail sidebar when available
- Genre taxonomy includes classic terms like `Drum & Bass`, `Jungle`,
  `Breakbeat`, `Hardcore` not always well-represented in Beatport

---

## Scaffold Location

```
Chronicle.Plugin.JunoDownload/
├── Chronicle.Plugin.JunoDownload.csproj
├── README.md
├── manifest.json
├── JunoDownloadPlugin.cs
└── Models/
    └── JunoRelease.cs
```
