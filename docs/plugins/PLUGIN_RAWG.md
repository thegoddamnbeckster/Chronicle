# Chronicle.Plugin.RAWG — Design Document

**Plugin ID:** `chronicle.plugin.rawg`
**Version:** 1.0.0
**Media Types:** Games (`game`)
**Auth:** API key (free at rawg.io)
**API:** RAWG REST API v2.0 — `https://api.rawg.io/api`

---

## Purpose

[RAWG](https://rawg.io/) is a large game database and discovery platform with
500,000+ games indexed. It provides rich metadata including platform support,
metacritic scores, screenshots, and genre tagging. RAWG's free API with no
rate limits (within reason) makes it an accessible complement or alternative
to IGDB for game metadata.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `game` | 2 | Cross-platform game metadata |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Game search | `GET /games?search={title}&key={key}` |
| Game detail | `GET /games/{id}?key={key}` |
| Game screenshots | `GET /games/{id}/screenshots?key={key}` |
| Game trailers | `GET /games/{id}/movies?key={key}` |
| Platform list | `GET /platforms?key={key}` |
| Genre list | `GET /genres?key={key}` |
| Developer detail | `GET /developers/{id}?key={key}` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | RAWG API Key | Password | Yes | rawg.io/apidocs |
| `include_screenshots` | Fetch Screenshots | Boolean | No | Default: true |
| `language` | Language | Dropdown | No | Default: `en` |

---

## Fields Populated

```
title, overview (description), year (released), genres,
cast (developers, publishers), rating, poster_url (background_image),
platforms, metacritic, esrb_rating, playtime, rawg_id, rawg_slug
```

---

## Rate Limits

- Free API key: no hard limit stated; reasonable use expected
- Add 200 ms delays between requests
- Cache responses for 7 days minimum

---

## Implementation Notes

- RAWG's `playtime` field (average hours) is a useful statistic for games
  — store in `metadata_json`
- `esrb_rating` and `rating` (RAWG community score) are both available
- `background_image` is a full-size screenshot used as the background —
  map to `BackdropUrl` in `MediaMetadata`
- The `short_screenshots` array provides additional artwork
- `slug` is RAWG's URL-friendly identifier — store alongside the numeric
  `id` in `media_external_ids` with source `rawg`
- Developers and publishers are separate arrays in the response

---

## Scaffold Location

```
Chronicle.Plugin.RAWG/
├── Chronicle.Plugin.RAWG.csproj
├── README.md (this document)
├── manifest.json
├── RAWGPlugin.cs
└── Models/
    ├── RawgGame.cs
    └── RawgPlatform.cs
```
