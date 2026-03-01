# Chronicle.Plugin.RottenTomatoes — Design Document

**Plugin ID:** `chronicle.plugin.rottentomatoes`
**Version:** 1.0.0
**Media Types:** Movies (`movie`), TV (`tv`)
**Auth:** API key (partner program — requires application)
**API:** Rotten Tomatoes API — `https://api.rottentomatoes.com/api/public/v2.0`

---

## Purpose

[Rotten Tomatoes](https://www.rottentomatoes.com/) provides the well-known
Tomatometer (critic score) and Audience Score for movies and TV shows.
This plugin enriches Chronicle entries with RT scores and certified-fresh
status. It is a **ratings enrichment** plugin, not a primary metadata source.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `movie` | 7 | Tomatometer + Audience Score |
| `tv` | 7 | Tomatometer + Audience Score |

---

## API Overview

The official Rotten Tomatoes API requires a partner application. A widely-used
alternative is the unofficial API endpoint which powers the RT website itself.

| Operation | Endpoint |
|-----------|---------|
| Movie search | `GET /movies.json?q={title}&apikey={key}` |
| Movie detail | `GET /movies/{id}.json?apikey={key}` |
| Movie reviews | `GET /movies/{id}/reviews.json?apikey={key}` |
| TV series search | `GET /tv_shows.json?q={title}&apikey={key}` |
| TV series detail | `GET /tv_shows/{id}.json?apikey={key}` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Rotten Tomatoes API Key | Password | Yes | Requires partner application |
| `include_reviews` | Fetch Critic Reviews | Boolean | No | Default: false (extra request) |
| `max_reviews` | Max Reviews to Fetch | Number | No | Default: 5 |

---

## Fields Populated

```
tomatometer_score, audience_score, critic_consensus,
certified_fresh, tomatometer_count, audience_count,
top_reviews (stored in metadata_json)
```

---

## Rate Limits

- Official API: 10,000 req/day (partner tier)
- Implement caching — RT scores update at most weekly
- Respect `Retry-After` headers on 429 responses

---

## Implementation Notes

- Rotten Tomatoes IDs are internal (`ebert_meyer_award`, numeric); cross-reference
  via IMDB ID where possible using the search endpoint
- `tomatometer_status` values: `Certified-Fresh`, `Fresh`, `Rotten`
- `audience_status` values: `Upright`, `Spilled`
- For TV shows, season-level scores are separate from series-level scores
- Store RT scores in `metadata_json` as they don't map to `MediaMetadata.Rating`
  directly (two separate scores)

---

## Scaffold Location

```
Chronicle.Plugin.RottenTomatoes/
├── Chronicle.Plugin.RottenTomatoes.csproj
├── README.md (this document)
├── manifest.json
├── RottenTomatoesPlugin.cs
└── Models/
    ├── RTMovie.cs
    ├── RTSeries.cs
    └── RTReview.cs
```
