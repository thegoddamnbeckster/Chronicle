# Chronicle.Plugin.Genius — Design Document

**Plugin ID:** `chronicle.plugin.genius`
**Version:** 1.0.0
**Media Types:** Music (`music`), Artists (`artist`)
**Auth:** API token (free at genius.com/api-clients)
**API:** Genius API v1 — `https://api.genius.com`

---

## Purpose

[Genius](https://genius.com/) (formerly Rap Genius) is the world's largest
lyrics and music annotation platform. Its database covers virtually every
commercially released song with crowdsourced annotations, verified artist
commentary, and rich editorial content. This plugin enriches Chronicle music
entries with lyrics metadata, annotation counts, and Genius editorial
descriptions — complementing the factual metadata from MusicBrainz/Discogs.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 6 | Track lyrics metadata and annotations |
| `artist` | 7 | Artist profile and description |

---

## API Overview

Base URL: `https://api.genius.com`
Auth header: `Authorization: Bearer {token}`

| Endpoint | Description |
|----------|-------------|
| `GET /search?q={query}` | Full-text search across songs |
| `GET /songs/{id}` | Song detail (metadata, not lyrics) |
| `GET /artists/{id}` | Artist profile |
| `GET /artists/{id}/songs` | Artist's songs (paginated) |
| `GET /referents?song_id={id}` | Annotations for a song |
| `GET /web_pages/lookup?canonical_url={url}` | Look up by URL |

> **Note:** The API returns metadata only. Lyrics are not available via
> the API — they require HTML scraping of `genius.com/song-title-lyrics`.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `access_token` | Genius Access Token | Password | Yes | genius.com/api-clients |
| `fetch_annotations` | Fetch Annotation Count | Boolean | No | Default: true |
| `fetch_description` | Fetch Artist Description | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview (song description/about), cast (artists),
poster_url (header image), genius_id, genius_url,
metadata_json: { annotation_count, pyongs_count,
                 featured_artists, producer_artists,
                 writer_artists, release_date_components,
                 genius_song_url, description_html }
```

---

## Rate Limits

- 100 req/min with free token (enforced by HTTP 429)
- Cache song metadata for 30 days — Genius content is editorially stable
- Lyrics scraping (if implemented): apply same delays as RateYourMusic

---

## Implementation Notes

- Genius's primary value is **credits data**: `producer_artists`,
  `writer_artists`, and `featured_artists` are exposed in the API
  response and supplement the performer credits from MusicBrainz
- The `description` field contains the Genius editorial "about" text
  in a Markdown-like format; strip to plain text for `overview`
- Song IDs are stable integers — store in `media_external_ids` with
  source `genius`
- The `apple_music_id`, `spotify_id` fields may be present in the
  song response — harvest these as cross-reference IDs
- `custom_header_image_url` and `header_image_url` are usable as
  poster images when MusicBrainz/Discogs cover art is unavailable

---

## Scaffold Location

```
Chronicle.Plugin.Genius/
├── Chronicle.Plugin.Genius.csproj
├── README.md
├── manifest.json
├── GeniusPlugin.cs
└── Models/
    ├── GeniusSong.cs
    └── GeniusArtist.cs
```
