# Chronicle.Plugin.Steam — Design Document

**Plugin ID:** `chronicle.plugin.steam`
**Version:** 1.0.0
**Media Types:** Games (`game`)
**Auth:** Steam API key (free at steamcommunity.com/dev/apikey)
**API:** Steam Web API + Steam Store API

---

## Purpose

[Steam](https://store.steampowered.com/) is the world's largest PC game
distribution platform. Steam's APIs provide authoritative metadata for the
50,000+ games in its catalogue, including system requirements, achievement
counts, DLC lists, and playtime data for the authenticated user. This is one
of the most practically useful game plugins as most PC gamers use Steam.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `game` | 2 | PC game metadata |

---

## API Overview

| Operation | Endpoint | Notes |
|-----------|---------|-------|
| App detail | `GET https://store.steampowered.com/api/appdetails?appids={id}` | No auth needed |
| App list | `GET https://api.steampowered.com/ISteamApps/GetAppList/v2/?key={key}` | Full game list |
| Search | Steam Store search (scrape or store API) | No official search API |
| User owned games | `GET https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={key}&steamid={id}` | Auth + SteamID |
| User playtime | Included in `GetOwnedGames` response | |
| User achievements | `GET https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/` | |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Steam API Key | Password | Yes | steamcommunity.com/dev/apikey |
| `steam_id` | SteamID64 | Text | No | For owned games / playtime sync |
| `language` | Language | Dropdown | No | Default: `en` |
| `include_dlc` | Include DLC | Boolean | No | Default: false |
| `sync_playtime` | Sync Playtime to Chronicle | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview (about_the_game), year (release_date), genres,
cast (developers, publishers), rating (metacritic), poster_url (header_image),
backdrop_url (screenshots[0]), platforms (windows/mac/linux),
required_age, steam_app_id, steam_url, achievement_count,
dlc_count, categories, tags
```

---

## Rate Limits

- Store API (appdetails): ~200 req/5 min
- Steam Web API: 100,000 req/day with key
- The app list (500,000 items) should be downloaded once and cached locally

---

## Implementation Notes

- Steam AppID is the primary identifier — store in `media_external_ids`
  with source `steam`
- `appdetails` returns a wrapper object: `{ "{appid}": { "success": true, "data": {...} } }`
  — unwrap before parsing
- Header image URL: `https://cdn.akamai.steamstatic.com/steam/apps/{appid}/header.jpg`
- Screenshot URLs follow `https://cdn.akamai.steamstatic.com/steam/apps/{appid}/ss_{hash}.jpg`
- If `sync_playtime: true`, implement an `IImportProvider` interface alongside
  `IMetadataProvider` to import user's owned games and playtime into Chronicle
- `categories` includes: `Single-player`, `Multi-player`, `Co-op`, `Steam Achievements`,
  `Steam Workshop`, `Steam Trading Cards` — store in `metadata_json`

---

## Scaffold Location

```
Chronicle.Plugin.Steam/
├── Chronicle.Plugin.Steam.csproj
├── README.md (this document)
├── manifest.json
├── SteamPlugin.cs
└── Models/
    ├── SteamAppDetail.cs
    ├── SteamOwnedGame.cs
    └── SteamAppData.cs
```
