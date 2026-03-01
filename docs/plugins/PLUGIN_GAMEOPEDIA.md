# Chronicle.Plugin.Gameopedia — Design Document

**Plugin ID:** `chronicle.plugin.gameopedia`
**Version:** 1.0.0
**Media Types:** Games (`game`)
**Auth:** API key (commercial — gameopedia.com)
**API:** Gameopedia REST API — `https://api.gameopedia.com`

---

## Purpose

[Gameopedia](https://www.gameopedia.com/) is a commercial game metadata
provider used by retailers, OTT platforms, and gaming services. It specialises
in structured, publisher-verified game data including localised titles,
regional release dates, platform-specific SKUs, and PEGI/ESRB ratings.
Gameopedia is preferred for retail and commercial applications requiring
accurate, rights-cleared metadata.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `game` | 3 | Commercial-grade game metadata |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Game search | `GET /games?q={title}&lang={lang}` |
| Game by ID | `GET /games/{gameopedia_id}` |
| Game by EAN | `GET /games/barcode/{ean}` |
| Platform list | `GET /platforms` |
| Genre list | `GET /genres` |
| Regional data | `GET /games/{id}/regions` |

All requests use `X-API-Key: {key}` header.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Gameopedia API Key | Password | Yes | Commercial subscription |
| `language` | Language | Dropdown | No | `en`, `fr`, `de`, `es`, etc. |
| `region` | Region | Dropdown | No | `US`, `EU`, `GB`, `AU`, `JP` |
| `include_regional_data` | Fetch Regional Variants | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview, year, genres, cast (developer, publisher),
rating (user/critic), poster_url, platforms, esrb_rating,
pegi_rating, usk_rating, gameopedia_id, barcode, sku,
regional_titles, regional_release_dates
```

---

## Rate Limits

- Defined per commercial contract
- Cache all data aggressively — game metadata is stable post-release

---

## Implementation Notes

- Gameopedia is the commercial choice for retailers and game platform operators
  who need licensed, publisher-verified metadata
- Regional data (different titles, ratings, and release dates by territory)
  is Gameopedia's differentiator — store in `metadata_json.regions`
- Barcode/EAN lookup enables physical disc → digital record matching
- This plugin is for users with existing Gameopedia subscriptions

---

## Scaffold Location

```
Chronicle.Plugin.Gameopedia/
├── Chronicle.Plugin.Gameopedia.csproj
├── README.md (this document)
├── manifest.json
├── GameopediaPlugin.cs
└── Models/
    ├── GameopediaGame.cs
    └── GameopediaRegion.cs
```
