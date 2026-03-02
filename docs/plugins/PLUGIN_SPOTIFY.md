# Chronicle.Plugin.Spotify — Design Document

**Plugin ID:** `chronicle.plugin.spotify`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** OAuth 2.0 Client Credentials (or PKCE for user data)
**API:** Spotify Web API — `https://api.spotify.com/v1`

---

## Purpose

[Spotify](https://www.spotify.com/) is the world's largest music streaming
service. Its Web API provides comprehensive track, album, and artist metadata
including audio features (tempo, key, energy, danceability), high-resolution
album art, and popularity scores. This plugin is a primary metadata source
for modern mainstream releases.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 2 | Rich track metadata + audio features |
| `album` | 2 | Album detail, art, release date |
| `artist` | 2 | Artist profile, genres, popularity |

---

## API Overview

Base URL: `https://api.spotify.com/v1`
Auth header: `Authorization: Bearer {token}`
Token endpoint: `POST https://accounts.spotify.com/api/token`

| Endpoint | Description |
|----------|-------------|
| `GET /search?q={query}&type=track,album,artist` | Full-text search |
| `GET /tracks/{id}` | Track detail |
| `GET /tracks/{id}/audio-features` | Audio analysis (tempo, key, energy…) |
| `GET /albums/{id}` | Album with tracklist |
| `GET /artists/{id}` | Artist profile |
| `GET /artists/{id}/top-tracks` | Artist top tracks |
| `GET /artists/{id}/albums` | Artist discography |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `client_id` | Spotify Client ID | Password | Yes | developer.spotify.com |
| `client_secret` | Spotify Client Secret | Password | Yes | developer.spotify.com |
| `include_audio_features` | Fetch Audio Features | Boolean | No | Default: true |
| `market` | Market (ISO 3166-1) | Text | No | Default: `US` |

---

## Fields Populated

```
title, overview, year, genres, cast (artists), poster_url,
duration, spotify_id, spotify_url,
metadata_json: { isrc, spotify_popularity, explicit,
                 audio_features: { tempo, key, mode, energy,
                   danceability, valence, acousticness,
                   instrumentalness, loudness, speechiness },
                 available_markets, disc_number, track_number }
```

---

## Rate Limits

- 100 req/min (Client Credentials); bursting allowed
- Token TTL: 3,600 s — refresh before expiry
- Cache track/album metadata for 24 hours; audio features for 30 days

---

## Implementation Notes

- Spotify IDs are 22-character Base62 strings — store in
  `media_external_ids` with source `spotify`
- ISRC is included in track responses — harvest for cross-referencing
  with MusicBrainz, Musixmatch
- Audio features are stable and change rarely — safe to cache for 30 days
- `images` array contains album art at multiple resolutions; select the
  largest (index 0) for `poster_url`
- Genres are returned at the artist level, not track level — fetch artist
  data to populate genres for tracks
- Use Client Credentials flow (no user login required) for all
  metadata-only use cases

---

## Scaffold Location

```
Chronicle.Plugin.Spotify/
├── Chronicle.Plugin.Spotify.csproj
├── README.md
├── manifest.json
├── SpotifyPlugin.cs
└── Models/
    ├── SpotifyTrack.cs
    ├── SpotifyAlbum.cs
    ├── SpotifyArtist.cs
    └── SpotifyAudioFeatures.cs
```
