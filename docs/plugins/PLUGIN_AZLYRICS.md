# Chronicle.Plugin.AZLyrics — Design Document

**Plugin ID:** `chronicle.plugin.azlyrics`
**Version:** 1.0.0
**Media Types:** Music (`music`), Artists (`artist`)
**Auth:** None (web scraping — no public API)
**API:** AZLyrics — `https://www.azlyrics.com`

---

## Purpose

[AZLyrics](https://www.azlyrics.com/) is one of the largest free lyrics
databases on the internet, with an extremely broad catalogue covering
mainstream, classic rock, pop, country, and hip-hop going back decades.
While it has no API, its pages follow a highly consistent URL and HTML
structure that makes scraping reliable. This plugin is a lightweight
fallback for confirming track existence and retrieving song metadata
when API-based sources lack coverage.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 9 | Track existence check and basic metadata |
| `artist` | 9 | Artist song index |

---

## Data Available (scraped)

| Page | Data Extracted |
|------|---------------|
| `/lyrics/{artist}/{song}.html` | Song title, artist name, album (if listed) |
| `/{artist}/` | Artist song list |

URL convention:
```
https://www.azlyrics.com/lyrics/{artist_slug}/{song_slug}.html
https://www.azlyrics.com/{first_letter}/{artist_slug}.html
```
Where slugs are lowercase, spaces removed, special chars stripped.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 3000 |
| `user_agent` | User Agent | Text | No | Browser UA string |

---

## Fields Populated

```
title, cast (artists), azlyrics_url,
metadata_json: { azlyrics_song_url, azlyrics_artist_url }
```

---

## Rate Limits

- AZLyrics actively rate-limits bots — minimum 3,000 ms between requests
- Implement random jitter (±1,000 ms) and respect `Retry-After` headers
- Cache all data for 30 days — lyrics content is static
- IP bans are common with aggressive scraping — this plugin is purely
  a last-resort fallback for track confirmation, not primary metadata

---

## Implementation Notes

- AZLyrics' primary value for Chronicle is **track existence confirmation**
  and URL cross-referencing, not rich metadata
- The URL slug is deterministic from artist/title: lowercase, remove
  non-alphanumeric, remove spaces. E.g. "The Beatles" → `thebeatles`,
  "Hey Jude" → `heyjude` → URL: `/lyrics/thebeatles/heyjude.html`
- AZLyrics does **not** provide structured JSON — all data is in HTML
- `<div class="ringtone">` and adjacent elements contain the song title
  confirmation; scrape the `<title>` tag as the most reliable source
- This plugin should only be invoked when higher-priority plugins fail
  to match a track (priority 9 = lowest priority)
- Chronicle does not store or display lyrics — this plugin is used only
  for metadata enrichment and URL cross-referencing

---

## Scaffold Location

```
Chronicle.Plugin.AZLyrics/
├── Chronicle.Plugin.AZLyrics.csproj
├── README.md
├── manifest.json
├── AZLyricsPlugin.cs
└── Models/
    └── AZLyricsTrack.cs
```
