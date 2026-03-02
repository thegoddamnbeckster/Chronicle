# Chronicle.Plugin.Beatport — Design Document

**Plugin ID:** `chronicle.plugin.beatport`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** OAuth 2.0 (Beatport developer programme)
**API:** Beatport API v4 — `https://api.beatport.com/v4`

---

## Purpose

[Beatport](https://www.beatport.com/) is the world's leading electronic
music download store and catalogue, serving DJs and producers. It has
the most comprehensive metadata for electronic dance music — sub-genres
(Techno, Deep House, Drum & Bass, etc.), BPM, key, release labels, and
chart rankings. This plugin is the primary metadata source for all EDM
and DJ-oriented music in Chronicle.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 1 | Definitive EDM track metadata |
| `album` | 2 | Release (EP/LP) detail |
| `artist` | 2 | DJ/producer profile |

---

## API Overview

Base URL: `https://api.beatport.com/v4`
Auth header: `Authorization: Bearer {token}`

| Endpoint | Description |
|----------|-------------|
| `GET /catalog/tracks/?q={query}` | Track search |
| `GET /catalog/tracks/{id}/` | Track detail |
| `GET /catalog/releases/{id}/` | Release detail |
| `GET /catalog/artists/{id}/` | Artist detail |
| `GET /catalog/tracks/?artist_id={id}` | Tracks by artist |
| `GET /catalog/charts/?type=top-100` | Beatport top 100 chart |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `client_id` | Beatport Client ID | Password | Yes | developer.beatport.com |
| `client_secret` | Beatport Client Secret | Password | Yes | |
| `include_chart_rank` | Include Chart Rank | Boolean | No | Default: true |

---

## Fields Populated

```
title, year, genres, cast (artists), poster_url, duration,
beatport_id, beatport_url,
metadata_json: { bpm, key_name, key_camelot, isrc,
                 label_name, label_id, release_date,
                 chart_rank, mix_name, sub_genre,
                 encode_status, exclusive }
```

---

## Rate Limits

- Not publicly documented; treat as 60 req/min
- Cache track/release data for 7 days

---

## Implementation Notes

- Beatport is the **authoritative source** for electronic music — always
  prioritise it over generic sources for EDM sub-genre classification
- `key` is returned as both musical key (e.g. `A min`) and Camelot wheel
  notation (e.g. `8A`) — store both in metadata
- `bpm` is included directly in the track response
- Sub-genres (e.g. `Melodic House & Techno`, `Afro House`, `UK Garage`)
  are Beatport's fine-grained taxonomy — store as `metadata_json.sub_genre`
  and map the parent genre to Chronicle's `genres`
- Beatport IDs are stable integers — store in `media_external_ids`
  with source `beatport`
- `mix_name` (e.g. `Original Mix`, `Extended Mix`, `Radio Edit`) is
  important metadata for EDM releases — include in `metadata_json`

---

## Scaffold Location

```
Chronicle.Plugin.Beatport/
├── Chronicle.Plugin.Beatport.csproj
├── README.md
├── manifest.json
├── BeatportPlugin.cs
└── Models/
    ├── BeatportTrack.cs
    ├── BeatportRelease.cs
    └── BeatportArtist.cs
```
