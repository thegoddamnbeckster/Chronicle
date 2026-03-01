# Chronicle.Plugin.Audiobookshelf — Design Document

**Plugin ID:** `chronicle.plugin.audiobookshelf`
**Version:** 1.0.0
**Media Types:** Books (`book`), Audiobooks (`audiobook`), Podcasts (`podcast`)
**Auth:** Local URL + API key (user's own Audiobookshelf server)
**API:** Audiobookshelf REST API — `http://{host}:{port}/api`

---

## Purpose

[Audiobookshelf](https://www.audiobookshelf.org/) is a self-hosted audiobook
and podcast server. This Chronicle plugin bridges Audiobookshelf and Chronicle
— pulling metadata for audiobooks and podcasts from a user's local
Audiobookshelf instance. Like the TinyMediaManager plugin, this is a local
bridge that reads pre-scraped data rather than querying external APIs.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `audiobook` | 1 | From Audiobookshelf library |
| `book` | 3 | Book metadata from Audiobookshelf |
| `podcast` | 1 | Podcast feed metadata and episodes |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Library list | `GET /api/libraries` |
| Library items | `GET /api/libraries/{id}/items` |
| Item detail | `GET /api/items/{id}` |
| Item by ISBN | `GET /api/libraries/{id}/items?filter=isbn:{isbn}` |
| Search | `GET /api/libraries/{id}/search?q={query}` |
| Cover image | `GET /api/items/{id}/cover` |
| Podcast episodes | `GET /api/podcasts/{id}/episodes` |
| Health check | `GET /api/ping` |

All requests require `Authorization: Bearer {api_key}` header.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `server_url` | Audiobookshelf URL | Url | Yes | e.g. `http://localhost:13378` |
| `api_key` | API Key | Password | Yes | From ABS Settings → Users |
| `default_library` | Default Library | Text | No | Library ID or name |
| `sync_progress` | Sync Listening Progress | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview (description), year, genres, cast (authors, narrators),
publisher, isbn, poster_url, runtime (duration), language,
audiobookshelf_id, series_name, series_sequence,
metadata_json: { narrator, abridged, chapters, publish_year,
                 publisher, language, explicit }
```

---

## Rate Limits

- No rate limits — local server
- Add 50 ms delay between requests to avoid overloading the server

---

## Implementation Notes

- Audiobookshelf stores metadata in its own database, synced from
  embedded tags and online sources (Audible, Google Books, Open Library)
- The `media.metadata` object in ABS responses contains the rich metadata;
  `media.audioFiles` contains the actual audio track data
- Progress sync (`sync_progress: true`) would allow bidirectional sync —
  reading in-progress state from ABS and writing back to Chronicle;
  this is an advanced feature deferred to v2 of the plugin
- Podcast feeds include `feedUrl`, `episodeCount`, `autoDownloadEpisodes`
- ABS uses its own item IDs; store with source `audiobookshelf` in
  `media_external_ids`

---

## Scaffold Location

```
Chronicle.Plugin.Audiobookshelf/
├── Chronicle.Plugin.Audiobookshelf.csproj
├── README.md (this document)
├── manifest.json
├── AudiobookshelfPlugin.cs
└── Models/
    ├── AbsLibraryItem.cs
    ├── AbsBookMetadata.cs
    └── AbsPodcastEpisode.cs
```
