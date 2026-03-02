# Chronicle.Plugin.Deezer — Design Document

**Plugin ID:** `chronicle.plugin.deezer`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None for public endpoints; OAuth 2.0 for user data
**API:** Deezer API — `https://api.deezer.com`

---

## Purpose

[Deezer](https://www.deezer.com/) is a major global music streaming service
with 90+ million tracks. Its API is one of the most permissive among major
streaming platforms — many endpoints require no authentication. Deezer
provides a reliable, freely accessible source of mainstream music metadata
including BPM, ISRC, rank scores, and multilingual artist biographies.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 4 | Track metadata including BPM and ISRC |
| `album` | 4 | Album detail with release date and genres |
| `artist` | 4 | Artist profile with radio rank |

---

## API Overview

Base URL: `https://api.deezer.com`
No auth required for public catalogue endpoints.

| Endpoint | Description |
|----------|-------------|
| `GET /search?q={query}` | Full-text search (tracks by default) |
| `GET /search/track?q={query}` | Track search |
| `GET /track/{id}` | Track detail |
| `GET /album/{id}` | Album detail |
| `GET /artist/{id}` | Artist detail |
| `GET /artist/{id}/albums` | Artist discography |
| `GET /track/isrc:{isrc}` | Track by ISRC |
| `GET /album/upc:{upc}` | Album by UPC |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `app_id` | Deezer App ID | Text | No | For OAuth (user data only) |
| `app_secret` | Deezer App Secret | Password | No | For OAuth |
| `include_bpm` | Include BPM | Boolean | No | Default: true |

---

## Fields Populated

```
title, year, genres, cast (artists), poster_url (cover),
duration, deezer_id, deezer_link,
metadata_json: { isrc, upc, bpm, rank, explicit_lyrics,
                 contributors, available_countries, deezer_fans }
```

---

## Rate Limits

- 50 req/5 sec (unauthenticated); higher with OAuth token
- JSONP/CORS-friendly — can be queried from front-end if needed
- Cache track metadata for 24 hours

---

## Implementation Notes

- ISRC lookup (`/track/isrc:{isrc}`) and UPC lookup (`/album/upc:{upc}`)
  are the best cross-reference entry points
- Deezer IDs are stable integers — store in `media_external_ids`
  with source `deezer`
- `bpm` is included directly in the track response — no separate
  audio-features call needed (unlike Spotify)
- `genres.data` on album objects gives genre ID + name pairs
- `cover_xl` (1000×1000) is the highest resolution album art
- `contributors` array includes featured artists with role labels
  (`Artist`, `Featured`, `MainArtist`) — map to Chronicle cast
- No API key required for public metadata — ship with zero mandatory
  settings, making this the most frictionless streaming plugin

---

## Scaffold Location

```
Chronicle.Plugin.Deezer/
├── Chronicle.Plugin.Deezer.csproj
├── README.md
├── manifest.json
├── DeezerPlugin.cs
└── Models/
    ├── DeezerTrack.cs
    ├── DeezerAlbum.cs
    └── DeezerArtist.cs
```
