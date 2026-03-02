# Chronicle.Plugin.PrestoMusic — Design Document

**Plugin ID:** `chronicle.plugin.prestomusic`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (web scraping — no public API)
**API:** Presto Music — `https://www.prestomusic.com`

---

## Purpose

[Presto Music](https://www.prestomusic.com/) (formerly Presto Classical) is
a leading classical and jazz music specialist retailer in the UK. Its
catalogue has exceptionally detailed classical metadata: conductor, orchestra,
soloists, opus numbers, catalogue references (BWV, K., Op.), recording dates,
and record label details. This plugin is a primary classical music metadata
source for Chronicle, complementing AllMusic and Qobuz.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 2 | Classical track metadata (opus, catalogue refs) |
| `album` | 2 | Classical album with performer credits |
| `artist` | 2 | Composer and performer profiles |

---

## Data Available (scraped)

| Page | Data Extracted |
|------|---------------|
| `/classical/{category}/{id}/{slug}` | Album: composer, conductor, orchestra, soloists, label, UPC |
| `/classical/composers/{id}/{slug}` | Composer biography and works list |
| `/search/?q=` | Search across classical catalogue |

Presto pages include structured JSON-LD and rich HTML metadata sections.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 2000 |
| `include_tracklist` | Fetch Tracklist | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview (editorial review), year, genres, cast,
poster_url, prestomusic_id, prestomusic_url,
metadata_json: { composer, conductor, orchestra, soloists,
                 opus_number, catalogue_ref, label_name,
                 catalog_number, upc, period, recording_date,
                 prestomusic_rating, tracklist }
```

---

## Rate Limits

- No official rate limit; minimum 2,000 ms between requests
- Cache release data for 14 days

---

## Implementation Notes

- Presto Music's key differentiator is the **classical performer hierarchy**:
  `composer` → `conductor` → `orchestra` → `soloists` — map these to
  Chronicle's cast with appropriate roles
- Opus and catalogue references (BWV, K., Op., D.) should be stored in
  `metadata_json.catalogue_ref` and surface in Chronicle's search
- The editorial star rating (1–5) from Presto's reviewers is a useful
  quality signal — store as `metadata_json.prestomusic_rating`
- UPC barcode is visible on product pages — cross-reference with
  Discogs and MusicBrainz for release matching
- `period` (Baroque, Classical, Romantic, 20th Century, Contemporary)
  is Presto's era classification — map to `genres` or store in metadata

---

## Scaffold Location

```
Chronicle.Plugin.PrestoMusic/
├── Chronicle.Plugin.PrestoMusic.csproj
├── README.md
├── manifest.json
├── PrestoMusicPlugin.cs
└── Models/
    └── PrestoRelease.cs
```
