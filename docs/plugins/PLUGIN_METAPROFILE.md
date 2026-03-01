# Chronicle.Plugin.MetaProfile — Design Document

**Plugin ID:** `chronicle.plugin.metaprofile`
**Version:** 1.0.0
**Media Types:** TV (`tv`), Movies (`movie`)
**Auth:** API key (registration at metaprofile.tv)
**API:** metaprofile.tv API — `https://metaprofile.tv`

---

## Purpose

[metaprofile.tv](https://metaprofile.tv/) is a metadata aggregation service
for TV shows and movies. It consolidates data from multiple sources into
structured programme profiles, particularly useful for European broadcasters
and content producers who need standardised metadata records.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `tv` | 5 | Programme profiles |
| `movie` | 5 | Film profiles |

---

## API Overview

metaprofile.tv provides a REST API for registered developers.

| Operation | Endpoint |
|-----------|---------|
| Search | `GET /api/v1/search?q={title}&type={movie|tv}` |
| Title detail | `GET /api/v1/titles/{id}` |
| Season detail | `GET /api/v1/titles/{id}/seasons/{n}` |
| Episode detail | `GET /api/v1/episodes/{id}` |
| Image assets | `GET /api/v1/titles/{id}/images` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | metaprofile.tv API Key | Password | Yes | Register at metaprofile.tv |
| `language` | Language | Dropdown | No | Default: `en` |
| `region` | Region | Dropdown | No | `EU`, `US`, `GB` |

---

## Fields Populated

```
title, overview, year, genres, cast, director, poster_url,
backdrop_url, rating, runtime, metaprofile_id,
episode_count, season_count, status
```

---

## Rate Limits

- Check metaprofile.tv API terms for current limits
- Implement caching: metadata cacheable for 24–48 hours

---

## Implementation Notes

- metaprofile.tv aggregates from multiple upstream sources; treat its
  data as a secondary supplement rather than a primary source
- Store the metaprofile.tv ID in `media_external_ids` with source
  `metaprofile`
- The API endpoint structure should be verified against current
  documentation at metaprofile.tv at implementation time

---

## Scaffold Location

```
Chronicle.Plugin.MetaProfile/
├── Chronicle.Plugin.MetaProfile.csproj
├── README.md (this document)
├── manifest.json
├── MetaProfilePlugin.cs
└── Models/
    └── MetaProfileTitle.cs
```
