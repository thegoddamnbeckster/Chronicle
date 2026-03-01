# Chronicle.Plugin.Gracenote — Design Document

**Plugin ID:** `chronicle.plugin.gracenote`
**Version:** 1.0.0
**Media Types:** TV (`tv`), Movies (`movie`), Music (`music`, `album`, `artist`)
**Auth:** Partner credentials (Client ID + User ID — enterprise/commercial)
**API:** Gracenote Web API — `https://data.tmsapi.com/v1.1`

---

## Purpose

Gracenote (owned by Nielsen) is one of the world's largest entertainment
metadata databases, used by set-top boxes, smart TVs, and streaming platforms.
It provides rich TV schedule data, movie metadata, and music recognition
(via the legacy CDDB). This plugin targets the Gracenote TMS (Tribune Media
Services) REST API, which is the most accessible programmatic interface.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `tv` | 3 | Series and episode metadata |
| `movie` | 3 | Full movie details |
| `music` | 3 | Artist and album metadata |

> Priority 3 — Gracenote is an enterprise fallback; prefer TMDB/TheTVDB
> for primary lookups.

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Movie search | `GET /movies/search?q={title}&api_key={key}` |
| Movie by Gracenote ID | `GET /movies/{gnID}?api_key={key}` |
| TV series search | `GET /programs/search?q={title}&api_key={key}` |
| Series episodes | `GET /series/{rootId}/episodes?api_key={key}` |
| Airings (schedule) | `GET /movies/airings?startDateTime={dt}&api_key={key}` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Gracenote API Key | Password | Yes | Requires partner application |
| `country` | Country Code | Dropdown | No | Default: `US` |
| `preferred_image_size` | Image Size | Dropdown | No | `Sm`, `Md`, `Lg`, `Ms` |

---

## Fields Populated

```
title, overview, year, poster_url, backdrop_url, genres, cast,
directors, rating, runtime, network, tms_id, gracenote_id
```

---

## Rate Limits

- Rate limits are negotiated per partner contract
- Default test tier: ~1,000 req/day
- Implement caching aggressively — Gracenote data changes rarely

---

## Implementation Notes

- Gracenote requires a formal partner application; this plugin is for
  users who already have credentials
- TMS IDs map to the `tmsId` field in responses
- Image URLs use `https://tmsimg.com/assets/{path}`
- The `preferredImage` block in responses contains ready-to-use image URLs
- For music, the legacy CDDB XML API is separate; this plugin does not
  cover music recognition / disc ID lookup

---

## Scaffold Location

```
Chronicle.Plugin.Gracenote/
├── Chronicle.Plugin.Gracenote.csproj
├── README.md (this document)
├── manifest.json
├── GracenotePlugin.cs
└── Models/
    ├── GracenoteMovie.cs
    ├── GracenoteSeries.cs
    └── GracenoteImage.cs
```
