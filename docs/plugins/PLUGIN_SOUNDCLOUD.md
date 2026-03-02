# Chronicle.Plugin.SoundCloud — Design Document

**Plugin ID:** `chronicle.plugin.soundcloud`
**Version:** 1.0.0
**Media Types:** Music (`music`), Artists (`artist`)
**Auth:** OAuth 2.0 (client credentials or user token)
**API:** SoundCloud API v2 — `https://api.soundcloud.com`

---

## Purpose

[SoundCloud](https://soundcloud.com/) is a leading audio streaming and music
distribution platform popular with independent artists, DJs, and podcasters.
Its catalogue contains hundreds of millions of tracks not found on mainstream
streaming services. This plugin enriches Chronicle music entries with
SoundCloud-specific metadata including play counts, waveform data, and
direct stream permalinks.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 7 | Track metadata and stream stats |
| `artist` | 7 | Artist profile and follower counts |

---

## API Overview

Base URL: `https://api.soundcloud.com`
Auth header: `Authorization: OAuth {token}` or `?client_id={id}` param (public)

| Endpoint | Description |
|----------|-------------|
| `GET /tracks?q={query}` | Search tracks |
| `GET /tracks/{id}` | Track by ID |
| `GET /users/{id}` | User/artist profile |
| `GET /users/{id}/tracks` | Artist's tracks |
| `GET /resolve?url={permalink_url}` | Resolve permalink to resource |
| `GET /tracks/{id}/related` | Related tracks |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `client_id` | SoundCloud Client ID | Password | Yes | soundcloud.com/you/apps |
| `client_secret` | SoundCloud Client Secret | Password | No | For OAuth user flow |
| `include_reposts` | Include Reposts | Boolean | No | Default: false |

---

## Fields Populated

```
title, cast (artists), duration, poster_url (artwork),
soundcloud_id, soundcloud_permalink,
metadata_json: { play_count, likes_count, reposts_count,
                 waveform_url, genre, tag_list, license }
```

---

## Rate Limits

- ~50 req/min on client credentials; higher with user OAuth token
- Use `resolve` endpoint to look up by permalink instead of search
- Cache track metadata for 24 hours

---

## Implementation Notes

- SoundCloud track IDs are stable integers; store as external ID
- The `permalink_url` (e.g. `soundcloud.com/artist/track`) is the
  most reliable lookup key — use the `resolve` endpoint to get the
  full track object from a permalink
- `artwork_url` returns a 100×100 image; replace `-large` with
  `-t500x500` in the URL for higher resolution artwork
- SoundCloud's public API access has been restricted since 2019;
  some endpoints require app approval. The `client_id` embedded in
  the SoundCloud web app can be used as a fallback (fragile)
- Monetised/geo-blocked tracks will return 401/403 on stream — only
  metadata is needed for Chronicle

---

## Scaffold Location

```
Chronicle.Plugin.SoundCloud/
├── Chronicle.Plugin.SoundCloud.csproj
├── README.md
├── manifest.json
├── SoundCloudPlugin.cs
└── Models/
    ├── SoundCloudTrack.cs
    └── SoundCloudUser.cs
```
