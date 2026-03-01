# Chronicle.Plugin.IGDB — Design Document

**Plugin ID:** `chronicle.plugin.igdb`
**Version:** 1.0.0
**Media Types:** Games (`game`)
**Auth:** Twitch client ID + client secret (free at dev.twitch.tv)
**API:** IGDB API v4 — `https://api.igdb.com/v4`

---

## Purpose

[IGDB](https://www.igdb.com/) (Internet Game Database, owned by Twitch/Amazon)
is the most comprehensive and well-structured public game database available.
It covers games across all platforms with detailed metadata including genres,
game modes, player perspectives, storylines, and aggregated critic/user ratings.
IGDB is the primary recommended game metadata source for Chronicle.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `game` | 1 | All platforms, all genres |

---

## API Overview

IGDB uses a proprietary query language (Apicalypse) via HTTP POST.

| Operation | Endpoint | Query |
|-----------|---------|-------|
| Search | `POST /games` | `search "{title}"; fields *;` |
| Game by ID | `POST /games` | `where id = {id}; fields *;` |
| Cover art | `POST /covers` | `where game = {id}; fields url;` |
| Screenshots | `POST /screenshots` | `where game = {id}; fields url;` |
| Artworks | `POST /artworks` | `where game = {id}; fields url;` |
| Platforms | `POST /platforms` | `where id = ({ids}); fields name;` |
| Companies | `POST /companies` | `where id = ({ids}); fields name,url;` |
| Age ratings | `POST /age_ratings` | `where id = ({ids}); fields rating;` |

Auth: `Client-ID: {client_id}` + `Authorization: Bearer {access_token}` headers.
Token obtained via: `POST https://id.twitch.tv/oauth2/token?client_id=...&client_secret=...&grant_type=client_credentials`

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `client_id` | Twitch Client ID | Text | Yes | dev.twitch.tv — free |
| `client_secret` | Twitch Client Secret | Password | Yes | dev.twitch.tv |
| `preferred_image_size` | Image Size | Dropdown | No | `t_cover_big`, `t_1080p`, `t_screenshot_big` |
| `include_screenshots` | Fetch Screenshots | Boolean | No | Default: true |
| `include_videos` | Fetch Videos | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview (summary/storyline), year (first_release_date),
genres, cast (involved_companies), rating (aggregated_rating),
poster_url (cover), backdrop_url (screenshots), platforms,
game_modes, player_perspectives, themes, age_ratings,
igdb_id, igdb_url, status, series, franchise
```

---

## Rate Limits

- 4 req/sec, 500 req/month on free tier
- Paid tiers available for higher volume
- Cache all responses — game metadata changes rarely after release

---

## Implementation Notes

- IGDB cover image URLs follow the pattern
  `//images.igdb.com/igdb/image/upload/t_{size}/{image_id}.jpg`
  — prepend `https:` and choose a size modifier
- `first_release_date` is a Unix timestamp — convert to year
- `status` values: `0=Released`, `2=Alpha`, `3=Beta`, `4=Early Access`,
  `5=Offline`, `6=Cancelled`, `7=Rumoured`
- `aggregated_rating` (critic) and `rating` (user) are both available
- Platform IDs reference the `/platforms` endpoint — cache the platform
  list to avoid extra lookups
- Store IGDB game ID in `media_external_ids` with source `igdb`

---

## Scaffold Location

```
Chronicle.Plugin.IGDB/
├── Chronicle.Plugin.IGDB.csproj
├── README.md (this document)
├── manifest.json
├── IGDBPlugin.cs
└── Models/
    ├── IgdbGame.cs
    ├── IgdbCover.cs
    └── IgdbPlatform.cs
```
