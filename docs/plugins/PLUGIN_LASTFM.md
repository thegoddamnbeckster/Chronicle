# Chronicle.Plugin.LastFM — Design Document

**Plugin ID:** `chronicle.plugin.lastfm`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** API key (free at last.fm/api) + optional OAuth for user data
**API:** Last.fm API 2.0 — `https://ws.audioscrobbler.com/2.0/`

---

## Purpose

[Last.fm](https://www.last.fm/) is the world's most popular music scrobbling
service and social music platform. Its API provides rich music metadata
(tags, biographies, similar artists, top tracks/albums) alongside community
data (listener counts, scrobble counts, user-specific play history).
This plugin provides both metadata enrichment and — optionally — import of
a user's Last.fm scrobble history.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 5 | Track metadata and scrobble stats |
| `album` | 4 | Album info, tags, listener counts |
| `artist` | 4 | Biography, tags, similar artists |

---

## API Overview

All requests: `GET https://ws.audioscrobbler.com/2.0/?method={method}&api_key={key}&format=json`

| Method | Parameters | Returns |
|--------|-----------|---------|
| `artist.getInfo` | `artist=` or `mbid=` | Bio, tags, similar, stats |
| `artist.search` | `artist=` | Artist search results |
| `artist.getSimilar` | `artist=` | Similar artists list |
| `album.getInfo` | `artist=`, `album=` or `mbid=` | Album info, tracks, tags |
| `album.search` | `album=` | Album search results |
| `track.getInfo` | `artist=`, `track=` or `mbid=` | Track info, tags, similar |
| `track.search` | `track=` | Track search results |
| `user.getRecentTracks` | `user=`, `limit=`, `page=` | Scrobble history (OAuth) |
| `user.getTopArtists` | `user=`, `period=` | User top artists |
| `user.getTopAlbums` | `user=`, `period=` | User top albums |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Last.fm API Key | Password | Yes | last.fm/api/account/create |
| `username` | Last.fm Username | Text | No | For user-specific data |
| `session_key` | Session Key (OAuth) | Password | No | For scrobble history import |
| `include_similar` | Fetch Similar Artists | Boolean | No | Default: false |
| `max_similar` | Max Similar Artists | Number | No | Default: 5 |

---

## Fields Populated

```
title, overview (bio summary), genres (tags), cast (artists), rating,
poster_url (album art via MusicBrainz cross-ref), lastfm_url,
listener_count, scrobble_count, lastfm_tags,
metadata_json: { similar_artists, top_tracks, top_albums, wiki_content }
```

---

## Rate Limits

- 5 requests/sec recommended; 300/min hard limit
- User history endpoints paginate at 200 tracks/page
- Cache artist/album metadata for 24 hours

---

## Implementation Notes

- Last.fm tags are folksonomy labels (user-submitted) — filter to tags with
  count ≥ 10 to remove noise; store top 10 as `genres`
- Listener/playcount stats are highly dynamic — cache for 1 hour maximum
- For scrobble history import, implement `IImportProvider` in addition to
  `IMetadataProvider`; use `user.getRecentTracks` with pagination
- MusicBrainz IDs are included in responses — store in `media_external_ids`
  with source `musicbrainz` for cross-referencing
- The `wiki.summary` field contains a brief bio; `wiki.content` the full text

---

## Scaffold Location

```
Chronicle.Plugin.LastFM/
├── Chronicle.Plugin.LastFM.csproj
├── README.md
├── manifest.json
├── LastFMPlugin.cs
└── Models/
    ├── LastFMArtist.cs
    ├── LastFMAlbum.cs
    └── LastFMTrack.cs
```
