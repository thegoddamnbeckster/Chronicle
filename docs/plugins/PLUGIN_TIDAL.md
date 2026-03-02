# Chronicle.Plugin.Tidal — Design Document

**Plugin ID:** `chronicle.plugin.tidal`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** OAuth 2.0 Client Credentials
**API:** Tidal API v2 — `https://openapi.tidal.com/v2`

---

## Purpose

[Tidal](https://tidal.com/) is a high-fidelity music streaming service known
for lossless (FLAC) and spatial audio (Dolby Atmos / Sony 360) content.
Its catalogue emphasises quality over quantity and is particularly strong
for jazz, classical, and audiophile-grade releases. This plugin provides
format-quality metadata (MQA, Dolby Atmos availability) not available
from other streaming APIs.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 5 | Track metadata + audio quality flags |
| `album` | 5 | Album with lossless/Atmos availability |
| `artist` | 5 | Artist profile |

---

## API Overview

Base URL: `https://openapi.tidal.com/v2`
Auth header: `Authorization: Bearer {token}`
Token endpoint: `POST https://auth.tidal.com/v1/oauth2/token`

| Endpoint | Description |
|----------|-------------|
| `GET /tracks/{id}` | Track detail |
| `GET /albums/{id}` | Album detail |
| `GET /artists/{id}` | Artist detail |
| `GET /tracks/{id}/relationships/artists` | Track artist relationships |
| `GET /albums/{id}/relationships/tracks` | Album tracklist |
| `GET /search/tracks?query={q}` | Track search |
| `GET /search/albums?query={q}` | Album search |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `client_id` | Tidal Client ID | Password | Yes | developer.tidal.com |
| `client_secret` | Tidal Client Secret | Password | Yes | developer.tidal.com |
| `country_code` | Country Code | Text | No | Default: `US` |

---

## Fields Populated

```
title, year, cast (artists), poster_url, duration,
tidal_id, tidal_url,
metadata_json: { isrc, tidal_popularity, explicit,
                 audio_quality: { max_quality, dolby_atmos,
                   sony_360, mqa_available },
                 track_number, volume_number }
```

---

## Rate Limits

- OAuth token TTL: 86,400 s (24 hours) — cache and refresh
- API rate limits not publicly documented; treat as ~60 req/min
- Cache metadata for 24 hours

---

## Implementation Notes

- Tidal's API v2 uses JSON:API format — resources are under `data`,
  relationships are separate objects under `relationships`
- Audio quality flags (`audioQuality`: `LOSSLESS`, `HI_RES_LOSSLESS`,
  `DOLBY_ATMOS`, `SONY_360RA`) are key differentiators — store in
  `metadata_json.audio_quality`
- Tidal IDs are stable integers — store in `media_external_ids`
  with source `tidal`
- ISRC is included in track responses — harvest for cross-referencing
- `imagePath` for album art uses format:
  `https://resources.tidal.com/images/{uuid}/{w}x{h}.jpg`
  Use `1280x1280` for full-resolution cover art
- The Tidal developer portal (developer.tidal.com) grants API access
  for metadata use cases under the free tier

---

## Scaffold Location

```
Chronicle.Plugin.Tidal/
├── Chronicle.Plugin.Tidal.csproj
├── README.md
├── manifest.json
├── TidalPlugin.cs
└── Models/
    ├── TidalTrack.cs
    ├── TidalAlbum.cs
    └── TidalArtist.cs
```
