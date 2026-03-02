# Chronicle.Plugin.Bandcamp — Design Document

**Plugin ID:** `chronicle.plugin.bandcamp`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (public pages + unofficial JSON API)
**API:** Bandcamp — `https://bandcamp.com` (scraping + embedded JSON)

---

## Purpose

[Bandcamp](https://bandcamp.com/) is the premier platform for independent
music sales and discovery, with a strong community of artists across
underground, experimental, and niche genres. It has no official public API;
data is extracted from embedded JSON in page source (`data-tralbum`,
`data-band`). This plugin provides rich indie/underground metadata not
available in mainstream databases.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 7 | Track metadata |
| `album` | 6 | Album detail, tracklist, pricing |
| `artist` | 7 | Artist/label profile and discography |

---

## Data Available (embedded JSON)

Bandcamp pages embed structured JSON in HTML attributes:

| Page | JSON key | Data |
|------|----------|------|
| `/{artist}.bandcamp.com/album/{slug}` | `data-tralbum` | Tracks, prices, tags, art |
| `/{artist}.bandcamp.com` | `data-band` | Artist bio, location, genres |
| Bandcamp Daily / search | `application/ld+json` | Schema.org MusicAlbum |

Example `data-tralbum` (partial):
```json
{
  "title": "...", "artist": "...", "release_date": "...",
  "tags": [...], "trackinfo": [{ "title", "duration", "track_num" }],
  "art_id": 12345678
}
```

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 1500 |
| `include_tags` | Fetch Tags | Boolean | No | Default: true |
| `include_tracklist` | Fetch Tracklist | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview (album description), year, genres (tags),
cast (artists), poster_url (art), bandcamp_url, bandcamp_id,
metadata_json: { tags, tracklist, price_usd, currency,
                 is_name_your_price, label, location }
```

---

## Rate Limits

- No official rate limit; be polite — minimum 1,500 ms between requests
- Implement random jitter (±500 ms)
- Cache album pages for 14 days — Bandcamp catalogue is stable
- Bandcamp does not actively block scrapers but respects robots.txt

---

## Implementation Notes

- Use Bandcamp's official search (`bandcamp.com/search?q=`) to find
  artist/album pages; parse JSON-LD for structured results
- Album art URL: `https://f4.bcbits.com/img/a{art_id}_10.jpg` (original
  size) — the `art_id` is in `data-tralbum`
- Tags are Bandcamp's genre/mood labels — richer than most databases
  for underground genres; store as `genres` + `metadata_json.tags`
- Track URLs contain a `stream_url` node in `data-tralbum` which may
  be playable — Chronicle only needs metadata, not audio
- No stable numeric ID exists in the URL; use the Bandcamp URL slug
  (e.g. `artist.bandcamp.com/album/album-title`) as the external ID

---

## Scaffold Location

```
Chronicle.Plugin.Bandcamp/
├── Chronicle.Plugin.Bandcamp.csproj
├── README.md
├── manifest.json
├── BandcampPlugin.cs
└── Models/
    ├── BandcampAlbum.cs
    └── BandcampTrack.cs
```
