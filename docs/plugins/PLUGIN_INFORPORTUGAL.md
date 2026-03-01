# Chronicle.Plugin.InforPortugal — Design Document

**Plugin ID:** `chronicle.plugin.inforportugal`
**Version:** 1.0.0
**Media Types:** TV (`tv`), Broadcast schedules (`tv_schedule`)
**Auth:** API key (commercial subscription via InforPortugal S.A.)
**API:** InforPortugal TV Metadata API

---

## Purpose

[InforPortugal S.A.](https://www.inforportugal.pt/) is Portugal's primary
provider of TV programme metadata and listings. It supplies EPG data, programme
descriptions, and broadcast schedules to Portuguese-language broadcasters and
TV guide services. This plugin enables Chronicle to ingest Portuguese TV
metadata and schedule information.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `tv` | 4 | Portuguese TV programme metadata |
| `tv_schedule` | 2 | Portuguese broadcast schedules |

---

## API Overview

InforPortugal provides a SOAP/REST hybrid API for licensed partners. The
specific endpoint base URL is provided upon subscription.

| Operation | Description |
|-----------|------------|
| Programme search | Search by title, genre, or date range |
| Programme detail | Full metadata by InforPortugal programme ID |
| Channel schedule | EPG data for a channel on a given date |
| Channel list | All available channels in the licensed region |
| Series/season listing | Episodes grouped by series |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | InforPortugal API Key | Password | Yes | From InforPortugal S.A. |
| `api_endpoint` | API Base URL | Url | Yes | Provided by InforPortugal |
| `language` | Language | Dropdown | No | `pt`, `en` — default: `pt` |
| `country` | Country | Dropdown | No | `PT`, `BR` — default: `PT` |

---

## Fields Populated

```
title, overview, year, genres, cast, director,
broadcast_channel, broadcast_date, episode_number,
season_number, duration_minutes, content_rating,
inforportugal_id
```

---

## Rate Limits

- Defined per commercial contract
- Typical schedule data: cacheable for 24 hours
- Programme metadata: cacheable for 7 days

---

## Implementation Notes

- InforPortugal uses a proprietary programme ID scheme; store with
  source `inforportugal` in `media_external_ids`
- This plugin is primarily useful for users tracking Portuguese-language
  broadcast television
- API documentation is provided under NDA; endpoint structure may differ
  from what is documented here — update during implementation
- Responses may be in XML or JSON depending on the contract endpoint

---

## Scaffold Location

```
Chronicle.Plugin.InforPortugal/
├── Chronicle.Plugin.InforPortugal.csproj
├── README.md (this document)
├── manifest.json
├── InforPortugalPlugin.cs
└── Models/
    ├── InforPortugalProgramme.cs
    └── InforPortugalSchedule.cs
```
