# Chronicle.Plugin.Qobuz — Design Document

**Plugin ID:** `chronicle.plugin.qobuz`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** API token (app_id + app_secret from Qobuz developer programme)
**API:** Qobuz API — `https://www.qobuz.com/api.json/0.2`

---

## Purpose

[Qobuz](https://www.qobuz.com/) is a premium hi-res audio streaming and
download service, particularly strong in classical music, jazz, and
audiophile recordings. Its API provides detailed technical metadata
including maximum streaming quality (24-bit/192kHz), label information,
and editorial "Album of the Week" / "Qobuz Ideal Discography" flags.
This plugin is the preferred source for classical and hi-res audio metadata.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 5 | Track + hi-res quality metadata |
| `album` | 4 | Album with label, UPC, quality flags |
| `artist` | 5 | Artist biography (editorial) |

---

## API Overview

Base URL: `https://www.qobuz.com/api.json/0.2`
Auth params: `app_id={app_id}` + request-level `user_auth_token` or
             HMAC-MD5 request signature (varies by endpoint).

| Endpoint | Description |
|----------|-------------|
| `GET /track/get?track_id={id}` | Track detail |
| `GET /album/get?album_id={id}` | Album detail |
| `GET /artist/get?artist_id={id}` | Artist detail |
| `GET /catalog/search?query={q}&type=tracks` | Search |
| `GET /album/getFeatured?type=editor-picks` | Editorial picks |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `app_id` | Qobuz App ID | Password | Yes | Qobuz developer programme |
| `app_secret` | Qobuz App Secret | Password | Yes | For request signing |
| `include_goodies` | Include Extras (PDF booklets) | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview (album description), year, genres, cast (artists),
poster_url, duration, qobuz_id, qobuz_url,
metadata_json: { isrc, upc, label, catalog_number,
                 maximum_bit_depth, maximum_sampling_rate,
                 hires_streamable, hires_purchased_streamable,
                 qobuz_editor_pick, ideal_discography,
                 awards, goodies }
```

---

## Rate Limits

- No published rate limit; treat as ~60 req/min
- App credentials require approval from Qobuz developer programme
- Cache metadata for 7 days; editorial flags for 24 hours

---

## Implementation Notes

- Qobuz's primary value is **hi-res audio quality metadata** and
  **classical/jazz catalogue depth** — use it alongside AllMusic for
  classical releases
- `maximum_bit_depth` and `maximum_sampling_rate` indicate the highest
  available streaming quality (e.g. 24-bit / 192 kHz)
- `hires_streamable: true` flags that the album is available in hi-res —
  store as a boolean in `metadata_json`
- `label.name` + `catalog_number` (release catalogue number) are
  important for classical music identification
- Request signing: `sig = md5(method_name + params_values + secret)`;
  include as `request_sig` query parameter
- UPC lookup: `album/get?album_id={upc}` also works with UPC codes
  as the ID parameter

---

## Scaffold Location

```
Chronicle.Plugin.Qobuz/
├── Chronicle.Plugin.Qobuz.csproj
├── README.md
├── manifest.json
├── QobuzPlugin.cs
└── Models/
    ├── QobuzTrack.cs
    ├── QobuzAlbum.cs
    └── QobuzArtist.cs
```
