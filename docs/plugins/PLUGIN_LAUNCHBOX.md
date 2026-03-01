# Chronicle.Plugin.LaunchBox — Design Document

**Plugin ID:** `chronicle.plugin.launchbox`
**Version:** 1.0.0
**Media Types:** Games (`game`)
**Auth:** None required (public XML download); API key for faster access
**API:** LaunchBox Games Database — `https://gamesdb.launchbox-app.com`

---

## Purpose

The [LaunchBox Games Database](https://gamesdb.launchbox-app.com/) is a
community-built game metadata database associated with the LaunchBox/BigBox
frontend for PC game libraries and emulation. It provides a comprehensive
data export (XML) for offline use and a REST API. LaunchBox excels at retro
and emulated game metadata, arcade games, and platform coverage across
classic hardware.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `game` | 2 | Strong retro / emulation coverage |

---

## API Overview

**Option A — REST API (requires registration):**

| Operation | Endpoint |
|-----------|---------|
| Game search | `GET /api/GetGamesByName.php?name={title}&apiKey={key}` |
| Game by ID | `GET /api/GetGameByID.php?id={id}&apiKey={key}` |
| Platform list | `GET /api/GetPlatformsList.php?apiKey={key}` |
| Games by platform | `GET /api/GetGamesByPlatformID.php?platformID={id}&apiKey={key}` |
| Game images | `GET /api/GetGameImagesByGameID.php?gameID={id}&apiKey={key}` |

**Option B — XML bulk download (no auth needed):**
Download from `https://gamesdb.launchbox-app.com/Metadata.zip` — a
nightly-updated XML archive containing the full database.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | LaunchBox API Key | Password | No | Optional for REST API access |
| `use_bulk_download` | Use Bulk XML Download | Boolean | No | Default: false |
| `bulk_db_path` | Local DB Path | FilePath | No | Path to extracted Metadata XML |
| `image_region` | Image Region | Dropdown | No | `North America`, `Europe`, `Japan` |

---

## Fields Populated

```
title, overview, year, genres, cast (developers, publishers),
rating, poster_url (cover art), platform, esrb_rating,
max_players, cooperative, launchbox_id, release_date,
video_url (trailer), wikipedia_url
```

---

## Rate Limits

- REST API: reasonable use; ~1 req/sec recommended
- Bulk XML: nightly download limit (one per day)
- Cache REST responses for 7 days; bulk DB local, no limits

---

## Implementation Notes

- LaunchBox's best feature is artwork — front/back cover art, cartridge art,
  fan art, and banner images across hundreds of platforms
- For retro game emulation, LaunchBox IDs are widely used by emulation
  frontends — store in `media_external_ids` with source `launchbox`
- The XML bulk download is large (~500 MB) but enables offline lookups;
  index it in a local SQLite for fast searching
- Image types in the DB: `Box - Front`, `Box - Back`, `Screenshot - Gameplay`,
  `Clear Logo`, `Fanart - Background`, `Cart - Front`

---

## Scaffold Location

```
Chronicle.Plugin.LaunchBox/
├── Chronicle.Plugin.LaunchBox.csproj
├── README.md (this document)
├── manifest.json
├── LaunchBoxPlugin.cs
└── Models/
    ├── LaunchBoxGame.cs
    └── LaunchBoxImage.cs
```
