# Chronicle.Plugin.MetacriticMusic — Design Document

**Plugin ID:** `chronicle.plugin.metacriticmusic`
**Version:** 1.0.0
**Media Types:** Albums (`album`), Artists (`artist`)
**Auth:** None (web scraping — no public API)
**API:** Metacritic Music — `https://www.metacritic.com/music`

---

## Purpose

[Metacritic](https://www.metacritic.com/music/) aggregates professional
music critic reviews into a single Metascore (0–100) and User Score (0–10).
Its music section covers major album releases with quantitative critical
reception data. This plugin enriches Chronicle album entries with aggregated
critical consensus — the music equivalent of the TMDB/IMDB rating for films.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `album` | 5 | Metascore and user score |
| `artist` | 6 | Artist critical reputation data |

---

## Data Available (scraped)

| Page | Data Extracted |
|------|---------------|
| `/music/{slug}/` | Album Metascore, user score, review count, release date |
| `/person/{slug}/` | Artist Metascore average, highest-rated albums |

Metacritic embeds `application/ld+json` schema.org `MusicAlbum` markup
with aggregate rating on album pages.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 2000 |
| `include_review_count` | Fetch Review Count | Boolean | No | Default: true |

---

## Fields Populated

```
title, year, cast (artists), rating (metascore normalised 0–10),
metacritic_url,
metadata_json: { metascore, user_score, critic_review_count,
                 user_review_count, metacritic_slug,
                 must_see_album }
```

---

## Rate Limits

- Metacritic aggressively blocks scrapers — minimum 2,000 ms with jitter
- Rotate User-Agent and set Referer headers to mimic browser
- Cache all scores for 7 days; older albums can be cached for 30 days

---

## Implementation Notes

- Metacritic's primary value is the **Metascore** — a weighted average
  of professional critic reviews on a 0–100 scale
- Normalise Metascore to 0–10 for Chronicle's `rating` field:
  `rating = metascore / 10.0`
- The `user_score` is on a 0–10 scale already
- JSON-LD `aggregateRating.ratingValue` contains the Metascore directly
  — prefer this over scraping the rendered HTML score element
- Metacritic coverage skews toward major-label releases reviewed by
  mainstream publications — indie and underground releases often have
  no Metacritic entry
- `must_see_album` (Universal Acclaim, Generally Favorable, Mixed,
  Generally Unfavorable) is Metacritic's qualitative tier —
  derive from Metascore range and store in metadata

---

## Scaffold Location

```
Chronicle.Plugin.MetacriticMusic/
├── Chronicle.Plugin.MetacriticMusic.csproj
├── README.md
├── manifest.json
├── MetacriticMusicPlugin.cs
└── Models/
    └── MetacriticAlbum.cs
```
