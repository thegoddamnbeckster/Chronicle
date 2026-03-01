# Chronicle.Plugin.OMDb — Design Document

**Plugin ID:** `chronicle.plugin.omdb`
**Version:** 1.0.0
**Media Types:** Movies (`movie`), TV (`tv`)
**Auth:** API key (free tier available at omdbapi.com)
**API:** OMDb API — `https://www.omdbapi.com`

---

## Purpose

The [Open Movie Database (OMDb)](https://www.omdbapi.com/) is a free,
community-maintained movie and TV database with a simple REST API. It returns
IMDb-sourced data including ratings from IMDb, Rotten Tomatoes, and Metacritic
in a single request. Excellent as a lightweight alternative or supplement to
IMDb/TMDB with very low friction (free API key, no partner program).

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `movie` | 3 | Full movie metadata + multi-source ratings |
| `tv` | 3 | Series metadata; limited episode detail |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Search by title | `GET /?s={title}&type={movie|series|episode}&apikey={key}` |
| Detail by IMDb ID | `GET /?i={imdb_id}&plot=full&apikey={key}` |
| Detail by title | `GET /?t={title}&y={year}&type={movie|series}&apikey={key}` |
| Season detail | `GET /?i={imdb_id}&Season={n}&apikey={key}` |
| Episode detail | `GET /?i={imdb_id}&Season={s}&Episode={e}&apikey={key}` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | OMDb API Key | Password | Yes | Free at omdbapi.com |
| `plot` | Plot Length | Dropdown | No | `short`, `full` — default: `full` |
| `include_ratings` | Include All Ratings | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview, year, genres, cast, directors, writers,
poster_url, rating (IMDb), imdb_votes, runtime, content_rating,
awards, language, country, box_office, dvd_release,
rotten_tomatoes_score, metacritic_score, imdb_id
```

---

## Rate Limits

- Free tier: 1,000 req/day
- Patron tier (paid): 100,000 req/day
- Implement daily quota tracking; fall back gracefully when exhausted

---

## Implementation Notes

- OMDb is one of the simplest APIs to integrate — single JSON endpoint
- Ratings array in response contains IMDb, Rotten Tomatoes, Metacritic
  objects simultaneously — map each to `metadata_json`
- The `Poster` field returns an image URL directly from IMDb's CDN
- `N/A` strings in responses mean the field is not available — treat as null
- Type field: `movie`, `series`, `episode`
- OMDb data ultimately mirrors IMDb; for primary enrichment prefer TMDB,
  use OMDb for the multi-source ratings bundle

---

## Scaffold Location

```
Chronicle.Plugin.OMDb/
├── Chronicle.Plugin.OMDb.csproj
├── README.md (this document)
├── manifest.json
├── OMDbPlugin.cs
└── Models/
    └── OmdbTitle.cs
```
