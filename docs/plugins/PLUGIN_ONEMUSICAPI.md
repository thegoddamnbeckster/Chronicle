# Chronicle.Plugin.OneMusicAPI — Design Document

**Plugin ID:** `chronicle.plugin.onemusicapi`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** API key (free tier available — onemusicapi.com)
**API:** OneMusicAPI REST API — `https://api.onemusicapi.com`

---

## Purpose

[OneMusicAPI](https://www.onemusicapi.com/) is a unified music metadata API
that aggregates data from multiple sources (MusicBrainz, Discogs, Spotify,
etc.) and returns it through a single endpoint. This simplifies music metadata
enrichment by eliminating the need to individually integrate each upstream
source.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 3 | Aggregated track metadata |
| `album` | 3 | Aggregated album metadata |
| `artist` | 3 | Aggregated artist metadata |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Artist search | `GET /artist?q={name}&limit={n}` |
| Artist by ID | `GET /artist/{oma_id}` |
| Album search | `GET /album?q={title}&artist={artist}` |
| Album by ID | `GET /album/{oma_id}` |
| Track search | `GET /track?q={title}&artist={artist}` |
| Track by ID | `GET /track/{oma_id}` |
| Health check | `GET /health` |

All requests require `X-API-Key: {key}` header.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | OneMusicAPI Key | Password | Yes | From onemusicapi.com |
| `language` | Language | Dropdown | No | Default: `en` |
| `sources` | Data Sources | MultiSelect | No | `musicbrainz`, `discogs`, `spotify` |

---

## Fields Populated

```
title, overview, year, genres, cast (artists), label,
poster_url (album art), rating, tracklist,
musicbrainz_id, discogs_id, spotify_id,
metadata_json: { sources_used, isrc, upc, bpm }
```

---

## Rate Limits

- Free tier: 500 req/month
- Paid tiers: up to 100,000 req/month
- Responses aggregate multiple upstream sources — cache the combined
  result to avoid repeated aggregation costs

---

## Implementation Notes

- OneMusicAPI's value is in aggregation — it returns a unified object
  combining the best data from multiple sources rather than requiring
  per-source integration
- The `sources` response field indicates which upstream sources
  contributed data for each field — useful for audit/provenance tracking
- Cross-reference IDs (MusicBrainz, Discogs, Spotify) are often included
  in responses — store all of them in `media_external_ids`
- BPM data is included for tracks — store in `metadata_json`

---

## Scaffold Location

```
Chronicle.Plugin.OneMusicAPI/
├── Chronicle.Plugin.OneMusicAPI.csproj
├── README.md (this document)
├── manifest.json
├── OneMusicAPIPlugin.cs
└── Models/
    ├── OMArtist.cs
    ├── OMAlbum.cs
    └── OMTrack.cs
```
