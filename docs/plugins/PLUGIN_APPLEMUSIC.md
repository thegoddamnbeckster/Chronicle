# Chronicle.Plugin.AppleMusic — Design Document

**Plugin ID:** `chronicle.plugin.applemusic`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** MusicKit developer token (JWT signed with Apple private key)
**API:** Apple Music API — `https://api.music.apple.com/v1`

---

## Purpose

[Apple Music](https://music.apple.com/) is Apple's streaming service with a
catalogue of over 100 million songs. Its API provides editorial content, rich
genres, mood-based content categorisation, and iTunes identifiers that serve
as a bridge to legacy iTunes catalogue data. This plugin is a primary
metadata source for mainstream releases, particularly strong for editorial
notes and genre taxonomy.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 3 | Track metadata and editorial notes |
| `album` | 3 | Album detail and editorial content |
| `artist` | 3 | Artist profile and genres |

---

## API Overview

Base URL: `https://api.music.apple.com/v1`
Auth header: `Authorization: Bearer {developer_token}`

| Endpoint | Description |
|----------|-------------|
| `GET /catalog/{storefront}/search?term={q}&types=songs,albums,artists` | Search |
| `GET /catalog/{storefront}/songs/{id}` | Song detail |
| `GET /catalog/{storefront}/albums/{id}` | Album detail |
| `GET /catalog/{storefront}/artists/{id}` | Artist detail |
| `GET /catalog/{storefront}/songs?filter[isrc]={isrc}` | Lookup by ISRC |

Storefront = ISO 3166-1 country code (e.g. `us`, `gb`).

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `team_id` | Apple Team ID | Text | Yes | developer.apple.com |
| `key_id` | MusicKit Key ID | Text | Yes | developer.apple.com |
| `private_key_path` | Private Key (.p8) Path | Text | Yes | Path on server |
| `storefront` | Storefront (Country) | Text | No | Default: `us` |

---

## Fields Populated

```
title, overview (editorial notes), year, genres, cast (artists),
poster_url (artwork), duration, apple_music_id, itunes_id,
metadata_json: { isrc, content_rating, composer_name,
                 disc_number, track_number, preview_url,
                 editorial_notes, genre_names }
```

---

## Rate Limits

- Developer token JWT: 180-day TTL — regenerate well before expiry
- ~1,000 req/min for catalog lookups (undocumented; Apple throttles)
- Cache all metadata for 24 hours

---

## Implementation Notes

- Developer token is a JWT signed with ES256 using the `.p8` private key
  from Apple Developer account — generate with standard JWT library
- ISRC lookup via `filter[isrc]=` is the most precise match method
- Apple Music IDs are not stable across storefronts — always use ISRC
  as the primary cross-reference key
- `artwork.url` uses `{w}x{h}` template placeholders — replace with
  `500x500` for standardised cover art
- Editorial notes (`editorialNotes.standard`) contain Apple Music's own
  album/artist descriptions — useful for `overview` field
- MusicKit JS SDK is available for client-side playback preview (30s) —
  not required for metadata-only usage

---

## Scaffold Location

```
Chronicle.Plugin.AppleMusic/
├── Chronicle.Plugin.AppleMusic.csproj
├── README.md
├── manifest.json
├── AppleMusicPlugin.cs
└── Models/
    ├── AmSong.cs
    ├── AmAlbum.cs
    └── AmArtist.cs
```
