# Chronicle.Plugin.SimplyTV — Design Document

**Plugin ID:** `chronicle.plugin.simplytv`
**Version:** 1.0.0
**Media Types:** TV (`tv`), Broadcast schedules (`tv_schedule`)
**Auth:** API key (subscription — contact Simply.tv)
**API:** Simply.tv Metadata API

---

## Purpose

[Simply.tv](https://www.simply.tv/) provides TV metadata and EPG services
primarily for European markets. It is used by broadcasters and OTT platforms
to deliver programme information, channel logos, and schedules. This plugin
targets the Simply.tv metadata API to enrich Chronicle entries with European
broadcast TV data.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `tv` | 5 | Programme and series metadata |
| `tv_schedule` | 3 | Broadcast schedule / EPG |

---

## API Overview

Simply.tv provides a REST JSON API for licensed partners.

| Operation | Endpoint |
|-----------|---------|
| Programme search | `GET /v1/programmes?query={q}` |
| Programme by ID | `GET /v1/programmes/{id}` |
| Channel list | `GET /v1/channels` |
| Schedule | `GET /v1/schedules/{channel_id}/{date}` |
| Series detail | `GET /v1/series/{id}` |

All requests use `X-API-Key: {key}` header.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Simply.tv API Key | Password | Yes | Contact Simply.tv |
| `country` | Country | Dropdown | No | `FR`, `BE`, `CH`, `LU` |
| `language` | Language | Dropdown | No | Default: `fr` |

---

## Fields Populated

```
title, overview, genres, cast, director, broadcast_channel,
broadcast_date, episode_number, season_number, poster_url,
duration_minutes, simply_tv_id, content_rating
```

---

## Rate Limits

- Defined per subscription contract
- Typical: cacheable for 24 hours for schedule data
- Programme metadata cacheable for 7 days

---

## Implementation Notes

- Simply.tv is particularly strong for French-language TV content
- Programme IDs use a Simply.tv-proprietary scheme; store with
  source `simplytv` in `media_external_ids`
- The API documentation is provided to subscribers; endpoint
  structure may require confirmation during implementation
- Logos and images are served from a Simply.tv CDN

---

## Scaffold Location

```
Chronicle.Plugin.SimplyTV/
├── Chronicle.Plugin.SimplyTV.csproj
├── README.md (this document)
├── manifest.json
├── SimplyTVPlugin.cs
└── Models/
    ├── SimplyTVProgramme.cs
    └── SimplyTVSchedule.cs
```
