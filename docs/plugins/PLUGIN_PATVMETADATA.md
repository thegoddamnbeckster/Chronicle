# Chronicle.Plugin.PATVMetadata — Design Document

**Plugin ID:** `chronicle.plugin.patvmetadata`
**Version:** 1.0.0
**Media Types:** TV (`tv`), Broadcast schedules (`tv_schedule`)
**Auth:** API key (PA Media Group — requires commercial subscription)
**API:** PA Media TV Metadata API — `https://api.tvmetadata.pa.media`

---

## Purpose

[PA Media Group](https://www.pamediagroup.com/) (formerly the Press Association)
provides professional-grade TV listings and programme metadata used by
broadcasters, EPG providers, and TV guide applications across the UK and Europe.
This plugin targets PA's TV Metadata API for authoritative British and European
broadcast data.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `tv` | 4 | Programme metadata |
| `tv_schedule` | 2 | Broadcast schedule / EPG data |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Programme search | `GET /programmes?q={title}` |
| Programme by ID | `GET /programmes/{pa_id}` |
| Schedule by channel | `GET /schedules/{channel_id}?date={date}` |
| Channel list | `GET /channels` |
| Series/seasons | `GET /series/{series_id}/episodes` |

All endpoints require `Authorization: Bearer {token}` header.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | PA Media API Key | Password | Yes | Commercial subscription required |
| `region` | Region | Dropdown | No | `gb`, `ie`, `us`, `au` |
| `include_schedule` | Include Schedule Data | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview, year, genres, cast, directors, rating,
pa_programme_id, itvx_id, channel, broadcast_date,
episode_number, season_number, content_warnings
```

---

## Rate Limits

- Rate limits defined per contract; typical: 1,000–10,000 req/day
- Responses are cacheable for 24 hours for schedule data

---

## Implementation Notes

- PA Media is the authoritative source for British TV listings and
  programme metadata — preferred over TMDB for UK broadcast content
- PA Programme IDs are used by many UK EPG systems; store in
  `media_external_ids` with source `pa_media`
- The API follows a RESTful design with ISO 8601 dates throughout
- Content warnings use BBFC / Ofcom classification codes

---

## Scaffold Location

```
Chronicle.Plugin.PATVMetadata/
├── Chronicle.Plugin.PATVMetadata.csproj
├── README.md (this document)
├── manifest.json
├── PATVMetadataPlugin.cs
└── Models/
    ├── PAProgramme.cs
    ├── PASchedule.cs
    └── PAChannel.cs
```
