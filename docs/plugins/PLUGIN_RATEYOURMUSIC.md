# Chronicle.Plugin.RateYourMusic — Design Document

**Plugin ID:** `chronicle.plugin.rateyourmusic`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (web scraping — no public API)
**API:** RateYourMusic / Sonemic — `https://rateyourmusic.com`

---

## Purpose

[RateYourMusic](https://rateyourmusic.com/) (also known as Sonemic) is a
community-driven music database and social cataloguing site with a particularly
strong genre taxonomy. It uses a highly detailed, hierarchical genre system
(from broad genres down to micro-genres like "Dungeon Synth" or
"Proto-Progressive Rock") and community ratings that are often more nuanced
than mainstream databases. No public API exists; HTML scraping is required.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 6 | Track metadata |
| `album` | 5 | Full album detail with genre tags |
| `artist` | 6 | Artist profile and discography |

---

## Data Available (scraped)

| Page | Data Extracted |
|------|---------------|
| `/release/{slug}` | Title, artist, year, genres, descriptors, avg rating, rating count, cover |
| `/artist/{slug}` | Biography, genres, discography list |
| `/search` (POST) | Search by title or artist |

RateYourMusic embeds some structured data in `<script type="application/ld+json">`:

```json
{
  "@type": "MusicAlbum",
  "name": "...",
  "byArtist": { "name": "..." },
  "datePublished": "...",
  "aggregateRating": { "ratingValue": "...", "ratingCount": "..." }
}
```

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `user_agent` | User Agent | Text | No | Browser UA string |
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 2000 |
| `include_descriptors` | Fetch Descriptors | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview, year, genres (primary + secondary), rating,
poster_url (cover), rym_id, rym_url, descriptor_tags,
metadata_json: { rym_rating, rym_rating_count, descriptors,
                 primary_genres, secondary_genres }
```

---

## Rate Limits

- RateYourMusic actively discourages scraping; minimum 2,000 ms between requests
- Implement random jitter (±500 ms) to avoid detection
- Cache all data for 14 days minimum — RYM data is community-curated and stable
- IP bans are possible if scraping is aggressive — use respectfully

---

## Implementation Notes

- RYM's genre taxonomy is its primary value: it distinguishes dozens of
  sub-genres that AllMusic and Discogs do not differentiate
- "Descriptors" (mood/instrument/theme tags) are separate from genres and
  provide additional context — store in `metadata_json.descriptors`
- RYM rating is on a 0.5–5.0 scale (community average)
- No stable ID scheme exists in the URL; the slug is derived from
  artist + album name; store the URL slug as the external ID
- RateYourMusic ToS discourages automated access; consider this a low-priority
  fallback; always prefer APIs over scraping

---

## Scaffold Location

```
Chronicle.Plugin.RateYourMusic/
├── Chronicle.Plugin.RateYourMusic.csproj
├── README.md
├── manifest.json
├── RateYourMusicPlugin.cs
└── Models/
    └── RymRelease.cs
```
