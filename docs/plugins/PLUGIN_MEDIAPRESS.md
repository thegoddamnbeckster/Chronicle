# Chronicle.Plugin.MediaPress — Design Document

**Plugin ID:** `chronicle.plugin.mediapress`
**Version:** 1.0.0
**Media Types:** TV (`tv`), Movies (`movie`)
**Auth:** API key (registration at media-press.tv)
**API:** media-press.tv API — `https://www.media-press.tv`

---

## Purpose

[media-press.tv](https://www.media-press.tv/) is a European media metadata
and press information service specialising in TV and film. It aggregates
programme synopses, press kits, cast information, and promotional materials
for European broadcasters and distributors. This plugin brings that data
into Chronicle for European TV and film content.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `tv` | 5 | TV programme metadata |
| `movie` | 5 | Film metadata |

---

## API Overview

media-press.tv provides a REST/JSON API for accredited media professionals.

| Operation | Endpoint |
|-----------|---------|
| Programme search | `GET /api/programmes/search?q={title}` |
| Programme detail | `GET /api/programmes/{id}` |
| Press kit | `GET /api/programmes/{id}/presskit` |
| Images | `GET /api/programmes/{id}/images` |
| Cast & crew | `GET /api/programmes/{id}/credits` |

All requests require `Authorization: Bearer {token}` header.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | media-press.tv API Key | Password | Yes | Requires accreditation |
| `language` | Language | Dropdown | No | `en`, `fr`, `de`, `es` — default: `en` |
| `country` | Country | Dropdown | No | For region-specific content |
| `include_press_kits` | Download Press Kits | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview, year, genres, cast, director, poster_url,
backdrop_url, rating, runtime, media_press_id,
distributor, production_company, press_contact
```

---

## Rate Limits

- Varies per accreditation level
- Press kit downloads should be rate-limited to 1 per 5 seconds
- Cache press materials locally once downloaded

---

## Implementation Notes

- media-press.tv focuses on European content; best for French, German,
  and Spanish TV/film where TMDB coverage may be thin
- Press kit materials (PDFs, high-res images) are large; do not
  download unless explicitly requested
- Store media-press.tv programme IDs with source `mediapress` in
  `media_external_ids`

---

## Scaffold Location

```
Chronicle.Plugin.MediaPress/
├── Chronicle.Plugin.MediaPress.csproj
├── README.md (this document)
├── manifest.json
├── MediaPressPlugin.cs
└── Models/
    ├── MediaPressProgramme.cs
    └── MediaPressPressKit.cs
```
