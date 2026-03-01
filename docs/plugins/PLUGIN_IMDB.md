# Chronicle.Plugin.IMDb — Design Document

**Plugin ID:** `chronicle.plugin.imdb`
**Version:** 1.0.0
**Media Types:** Movies (`movie`), TV (`tv`)
**Auth:** IMDb API key (RapidAPI or official partner tier)
**API:** IMDb API via RapidAPI — `https://imdb-api.com` / `https://rapidapi.com`

---

## Purpose

IMDb (Internet Movie Database) is the world's most visited movie/TV reference
site. While IMDb does not offer a fully public free API, this plugin targets
the official IMDb developer API (where available) and the widely-used
RapidAPI wrappers as a fallback. IMDb ratings and IDs (`tt`-prefixed) are
the universal cross-reference standard across the industry.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `movie` | 2 | Full movie metadata |
| `tv` | 2 | Series, season, episode data |

---

## API Approach

Chronicle's IMDb plugin targets the **IMDb API on RapidAPI**
(`imdb-api.p.rapidapi.com`) as a pragmatic entry point. A configuration
option allows switching to a direct endpoint for users with official
IMDb developer access.

| Operation | Endpoint |
|-----------|---------|
| Search | `GET /en/API/Search/{api_key}/{title}` |
| Title detail | `GET /en/API/Title/{api_key}/{imdb_id}` |
| Ratings | `GET /en/API/Ratings/{api_key}/{imdb_id}` |
| Cast | `GET /en/API/FullCast/{api_key}/{imdb_id}` |
| Images | `GET /en/API/Images/{api_key}/{imdb_id}` |
| Awards | `GET /en/API/Awards/{api_key}/{imdb_id}` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | IMDb API Key | Password | Yes | From RapidAPI or official |
| `api_host` | API Host | Text | No | Default: `imdb-api.p.rapidapi.com` |
| `language` | Language | Dropdown | No | Default: `en` |
| `include_ratings` | Fetch Ratings | Boolean | No | Default: true |
| `include_cast` | Fetch Full Cast | Boolean | No | Default: false (extra request) |

---

## Fields Populated

```
title, overview, year, poster_url, genres, cast, directors, rating,
imdb_id, runtime, content_rating, awards, keywords, similar_titles
```

---

## Rate Limits

- RapidAPI free tier: 100 req/month (very limited)
- RapidAPI basic paid tier: ~10,000 req/month
- Official IMDb API: negotiated per contract
- Strongly recommended: cache all responses for 7+ days

---

## Implementation Notes

- IMDb IDs (`tt0000001`) are the universal cross-reference — always store
  in `media_external_ids` with source `imdb`
- The `imdb_rating` and `metacritic_rating` fields are both available from
  the Ratings endpoint
- Poster URLs returned by IMDb API are high-resolution — append `._V1_` size
  modifiers (e.g. `UX300`) if resizing is needed
- Type field in responses: `Movie`, `TVSeries`, `TVMiniSeries`, `TVEpisode`,
  `Short`, `Documentary`

---

## Scaffold Location

```
Chronicle.Plugin.IMDb/
├── Chronicle.Plugin.IMDb.csproj
├── README.md (this document)
├── manifest.json
├── IMDbPlugin.cs
└── Models/
    ├── IMDbTitle.cs
    ├── IMDbRatings.cs
    └── IMDbCast.cs
```
