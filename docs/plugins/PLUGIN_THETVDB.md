# Chronicle.Plugin.TheTVDB — Design Document

**Plugin ID:** `chronicle.plugin.thetvdb`
**Version:** 1.0.0
**Media Types:** TV (`tv`)
**Auth:** API key (free registration at thetvdb.com)
**API:** TheTVDB REST API v4 — `https://api4.thetvdb.com/v4`

---

## Purpose

Provides metadata for TV series, seasons, and episodes from
[TheTVDB](https://thetvdb.com/) — the de-facto community standard for TV
metadata. TheTVDB covers series IDs that all other tools (Plex, Kodi, Sonarr)
cross-reference, making it a high-priority metadata source for anything
television-related.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `tv` | 1 | Series-level metadata |
| `tv_season` | 1 | Season-level artwork and air dates |
| `tv_episode` | 1 | Individual episode metadata |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Auth (token) | `POST /login` |
| Search series | `GET /search?query={q}&type=series` |
| Series by ID | `GET /series/{id}/extended` |
| Seasons | `GET /series/{id}/seasons/official/extended` |
| Episodes | `GET /series/{id}/episodes/official?season={n}` |
| Episode by ID | `GET /episodes/{id}/extended` |
| Artwork | `GET /series/{id}/artworks` |
| Translation | `GET /series/{id}/translations/{lang}` |

Authentication uses a short-lived JWT obtained by posting the API key to
`/login`. Chronicle should cache and refresh this token automatically.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | TheTVDB API Key | Password | Yes | Free at thetvdb.com/dashboard |
| `language` | Preferred Language | Dropdown | No | Default: `eng` |
| `fallback_language` | Fallback Language | Dropdown | No | Default: `eng` |
| `include_all_seasons` | Fetch All Seasons | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview, year, poster_url, backdrop_url, genres, cast,
directors, rating, network, status, episode_count, season_count,
first_air_date, last_air_date, imdb_id, zap2it_id, official_site
```

---

## Rate Limits

- 100 requests/day on free tier; higher limits with subscription
- Token refresh required every 30 days
- Implement exponential back-off on 429

---

## Implementation Notes

- TheTVDB IDs are the cross-reference IDs used by Sonarr, Plex, Kodi —
  store the TVDB ID in `media_external_ids` with source `thetvdb`
- The v4 API uses a subscription model for bulk access; free tier is
  sufficient for single-item lookups
- Prefer `/extended` endpoints to minimise request count
- Series status values: `Continuing`, `Ended`, `Upcoming`, `Cancelled`

---

## Scaffold Location

```
Chronicle.Plugin.TheTVDB/
├── Chronicle.Plugin.TheTVDB.csproj
├── README.md (this document)
├── manifest.json
├── TheTVDBPlugin.cs
└── Models/
    ├── TvdbSeries.cs
    ├── TvdbEpisode.cs
    └── TvdbArtwork.cs
```
