# Chronicle.Plugin.TinyMediaManager — Design Document

**Plugin ID:** `chronicle.plugin.tinymediamanager`
**Version:** 1.0.0
**Media Types:** Movies (`movie`), TV (`tv`)
**Auth:** Local — no external auth required; accesses a running TMM instance
**API:** TinyMediaManager REST API — `http://localhost:{port}/api/v3`

---

## Purpose

[TinyMediaManager](https://www.tinymediamanager.org/) (TMM) is a popular
local media management application that scrapes metadata from multiple sources
(TMDB, TheTVDB, Trakt, etc.) and stores it alongside media files. When a user
already runs a TinyMediaManager instance, this Chronicle plugin can pull
pre-scraped, locally-cached metadata directly from TMM rather than
re-querying upstream APIs.

This is a **local bridge** plugin — it reads data TMM has already fetched.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `movie` | 2 | From TMM movie library |
| `tv` | 2 | From TMM TV show library |

---

## API Overview

TinyMediaManager exposes a REST API when the API server feature is enabled.

| Operation | Endpoint |
|-----------|---------|
| Movie list | `GET /api/v3/movie` |
| Movie by ID | `GET /api/v3/movie/{tmm_id}` |
| Movie search | `GET /api/v3/movie?title={q}` |
| TV show list | `GET /api/v3/tvshow` |
| TV show by ID | `GET /api/v3/tvshow/{tmm_id}` |
| TV show search | `GET /api/v3/tvshow?title={q}` |
| Season detail | `GET /api/v3/tvshow/{id}/season/{n}` |
| Episode detail | `GET /api/v3/tvshow/{id}/season/{s}/episode/{e}` |
| Health check | `GET /api/v3/` |

All requests use Basic Auth with the TMM API username/password.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `tmm_url` | TinyMediaManager URL | Url | Yes | e.g. `http://localhost:4000` |
| `username` | TMM API Username | Text | No | If auth is enabled |
| `password` | TMM API Password | Password | No | If auth is enabled |
| `prefer_local_images` | Use TMM local image paths | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview, year, genres, cast, directors, rating,
poster_url, backdrop_url, imdb_id, tmdb_id, tvdb_id,
runtime, release_date, status, tagline, production_company
```

---

## Rate Limits

- No rate limits — this is a local service
- Avoid hammering the local TMM server; 50 ms delay between requests

---

## Implementation Notes

- TMM must have the API server feature enabled in its settings
- TMM API returns `NFO`-compatible data structures
- Local image paths (e.g. `/media/movies/Inception/poster.jpg`) should
  be served via Chronicle's own file server or mapped to Chronicle's
  media directory; do not copy images unless explicitly configured
- TMM ID is internal; cross-reference via `imdb_id` / `tmdb_id` fields
  which TMM includes in responses
- The API requires TMM 4.x; version 3.x uses a different endpoint schema
- Enable "API Server" in TMM Preferences → General → API Settings

---

## Scaffold Location

```
Chronicle.Plugin.TinyMediaManager/
├── Chronicle.Plugin.TinyMediaManager.csproj
├── README.md (this document)
├── manifest.json
├── TinyMediaManagerPlugin.cs
└── Models/
    ├── TmmMovie.cs
    ├── TmmTvShow.cs
    └── TmmEpisode.cs
```
