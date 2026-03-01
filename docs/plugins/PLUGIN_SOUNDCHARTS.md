# Chronicle.Plugin.Soundcharts — Design Document

**Plugin ID:** `chronicle.plugin.soundcharts`
**Version:** 1.0.0
**Media Types:** Music (`music`), Artists (`artist`)
**Auth:** API key + App ID (paid subscription)
**API:** Soundcharts REST API v2 — `https://customer.api.soundcharts.com/api/v2`

---

## Purpose

[Soundcharts](https://soundcharts.com/) is a real-time music intelligence
platform tracking artists across streaming platforms, radio, charts, and social
media. This plugin enriches Chronicle music entries with streaming performance
data, chart positions, playlist placements, and audience metrics — data not
available from traditional metadata databases.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 7 | Track/album streaming analytics |
| `artist` | 7 | Artist popularity and reach metrics |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Artist search | `GET /artist?name={artist}` |
| Artist detail | `GET /artist/{uuid}` |
| Artist social | `GET /artist/{uuid}/social/audience` |
| Artist chart ranking | `GET /artist/{uuid}/charts` |
| Song detail | `GET /song/{uuid}` |
| Song streams | `GET /song/{uuid}/spotify/streams` |
| Song chart positions | `GET /song/{uuid}/chart-positions` |
| Playlist placements | `GET /song/{uuid}/playlists` |

All requests require headers:
```
x-app-id: {app_id}
x-api-key: {api_key}
```

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `app_id` | Soundcharts App ID | Text | Yes | From Soundcharts dashboard |
| `api_key` | Soundcharts API Key | Password | Yes | From Soundcharts dashboard |
| `include_social` | Include Social Metrics | Boolean | No | Default: false |
| `include_charts` | Include Chart Positions | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview, genres, poster_url,
metadata_json: { spotify_streams, spotify_listeners,
                 chart_positions: [{ chart, position, date }],
                 playlist_count, social_followers,
                 soundcharts_uuid }
```

---

## Rate Limits

- Varies by subscription tier (Starter, Pro, Enterprise)
- Starter: ~100 req/day
- Implement aggressive caching — streaming data changes daily at most

---

## Implementation Notes

- Soundcharts is a commercial product aimed at music industry professionals;
  this plugin is for users with existing subscriptions
- Data is analytics-focused — more useful for active music monitoring
  than one-time metadata enrichment
- UUIDs are Soundcharts-internal; use Spotify/ISRC IDs for cross-referencing
- The `metadata_json` approach is the right fit — these analytics fields
  don't map to `MediaMetadata` core properties
- Consider implementing as `IReportPlugin` rather than `IMetadataProvider`
  for a better architectural fit

---

## Scaffold Location

```
Chronicle.Plugin.Soundcharts/
├── Chronicle.Plugin.Soundcharts.csproj
├── README.md (this document)
├── manifest.json
├── SoundchartsPlugin.cs
└── Models/
    ├── SoundchartsArtist.cs
    └── SoundchartsSong.cs
```
