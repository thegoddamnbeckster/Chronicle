# Chronicle.Plugin.AllMusic — Design Document

**Plugin ID:** `chronicle.plugin.allmusic`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (web scraping — no public API)
**API:** AllMusic website — `https://www.allmusic.com`

---

## Purpose

[AllMusic](https://www.allmusic.com/) is one of the oldest and most
comprehensive music reference databases, providing editorial reviews, genre
classifications, mood/theme descriptors, similar artist recommendations, and
a rich discography for virtually every major artist. Because AllMusic has no
public API, this plugin uses structured HTML scraping to extract metadata.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 5 | Track and release metadata |
| `album` | 4 | Full album reviews and ratings |
| `artist` | 4 | Biography, styles, moods, influences |

---

## Data Available (scraped)

| Page | Data Extracted |
|------|---------------|
| `/artist/{slug}` | Biography, genres, styles, moods, themes, influences, similar artists |
| `/album/{slug}` | Review, rating (1–5 stars), tracklist, release year, label |
| Search (`/search/artists`, `/search/albums`) | Name, image, genre, year |

AllMusic embeds JSON-LD structured data in most pages — prefer this over raw
HTML parsing where available.

```json
{
  "@type": "MusicAlbum",
  "name": "...",
  "byArtist": { "name": "..." },
  "datePublished": "...",
  "genre": ["..."]
}
```

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `user_agent` | User Agent | Text | No | Browser UA string |
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 1500 |
| `include_review` | Fetch Editorial Review | Boolean | No | Default: true |
| `include_similar` | Fetch Similar Artists | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview (editorial review), year, genres, styles (moods/themes),
cast (artists), label, rating (1–5 star), similar_artists,
allmusic_id, allmusic_url, cover_url
```

---

## Rate Limits

- No official limit; scrape politely: minimum 1,500 ms between requests
- AllMusic may block scrapers — rotate User-Agent, respect `robots.txt`
- Cache all pages for 7 days minimum (editorial data changes rarely)

---

## Implementation Notes

- AllMusic slugs are derived from artist/album names with AllMusic-specific
  normalisation (e.g. spaces → `-`, special chars stripped)
- The editorial rating is a 1–5 star system (displayed as half-stars 0.5–5.0)
  stored as a decimal in `metadata_json.allmusic_rating`
- Genre taxonomy uses AllMusic's own style/mood/theme hierarchy which is
  richer than simple genre tags — store styles in `metadata_json.styles`
  and moods in `metadata_json.moods`
- `AllMusicClassical` (a separate plugin) covers the classical sub-database
  at `allmusic.com/genre/classical-*`

---

## Scaffold Location

```
Chronicle.Plugin.AllMusic/
├── Chronicle.Plugin.AllMusic.csproj
├── README.md
├── manifest.json
├── AllMusicPlugin.cs
└── Models/
    ├── AllMusicAlbum.cs
    └── AllMusicArtist.cs
```
