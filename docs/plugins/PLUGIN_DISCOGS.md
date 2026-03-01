# Chronicle.Plugin.Discogs — Design Document

**Plugin ID:** `chronicle.plugin.discogs`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** OAuth 1.0a or personal access token (free at discogs.com)
**API:** Discogs REST API v2 — `https://api.discogs.com`

---

## Purpose

[Discogs](https://www.discogs.com/) is the world's largest community-built
database of recorded music, specialising in physical releases (vinyl, CD,
cassette). It provides rich metadata about releases, master recordings, artists,
and labels with a particular strength in release variant tracking, pressing
information, and release date / country data.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 2 | Physical release metadata |
| `album` | 1 | Full release and master release detail |
| `artist` | 2 | Artist discography |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Search | `GET /database/search?q={query}&type={release|master|artist|label}` |
| Release detail | `GET /releases/{id}` |
| Master release | `GET /masters/{id}` |
| Artist detail | `GET /artists/{id}` |
| Artist releases | `GET /artists/{id}/releases` |
| Label detail | `GET /labels/{id}` |
| Label releases | `GET /labels/{id}/releases` |
| Image | `GET {image_url}` (direct CDN URL from response) |

All requests require `Authorization: Discogs token={token}` header.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `personal_token` | Discogs Personal Access Token | Password | Yes | discogs.com/settings/developers |
| `currency` | Currency | Dropdown | No | `USD`, `GBP`, `EUR` — default: `USD` |
| `prefer_master` | Prefer Master Releases | Boolean | No | Default: true |
| `include_tracklist` | Fetch Track Listing | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview, year, genres, cast (artists/musicians),
label, catalog_number, country, formats (vinyl/CD/etc.),
tracklist, discogs_id, discogs_master_id, cover_image_url,
release_date, pressing_info, barcode, community_rating
```

---

## Rate Limits

- Authenticated: 60 req/min
- Unauthenticated: 25 req/min
- Implement 1-second delays between requests to stay within limits
- Cache all responses — Discogs data is highly stable

---

## Implementation Notes

- Discogs distinguishes **Releases** (specific pressings) from
  **Master Releases** (canonical recording across all pressings)
  — for most Chronicle use cases, master releases are preferred
- The `format` array describes the physical medium: `Vinyl`, `CD`,
  `Cassette`, `Digital File`, `DVD`, etc. — store in `metadata_json`
- Tracklist items have `position`, `title`, `duration` — store in
  `metadata_json.tracks` as an array
- Discogs image URLs are time-limited CDN links — store the Discogs ID
  and re-fetch images if the URL expires
- Community `have` and `want` counts are available and can be stored
  as social proof metrics in `metadata_json`
- Store Discogs release ID in `media_external_ids` with source `discogs`

---

## Scaffold Location

```
Chronicle.Plugin.Discogs/
├── Chronicle.Plugin.Discogs.csproj
├── README.md (this document)
├── manifest.json
├── DiscogsPlugin.cs
└── Models/
    ├── DiscogsRelease.cs
    ├── DiscogsMaster.cs
    ├── DiscogsArtist.cs
    └── DiscogsTrack.cs
```
