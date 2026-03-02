# Chronicle.Plugin.Musixmatch — Design Document

**Plugin ID:** `chronicle.plugin.musixmatch`
**Version:** 1.0.0
**Media Types:** Music (`music`), Artists (`artist`)
**Auth:** API key (free tier at developer.musixmatch.com)
**API:** Musixmatch API v1.1 — `https://api.musixmatch.com/ws/1.1`

---

## Purpose

[Musixmatch](https://www.musixmatch.com/) is the world's largest lyrics
database, powering lyrics display in Spotify, Apple Music, Amazon Music,
and many other services. Its API provides lyrics metadata, track ratings,
and genre information with ISRC-based track matching. This plugin
supplements Chronicle's music entries with lyrics availability data,
community ratings, and cross-service ISRC lookups.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 6 | Track metadata and lyrics availability |
| `artist` | 7 | Artist profile |

---

## API Overview

Base URL: `https://api.musixmatch.com/ws/1.1`
All requests append `apikey={key}` as a query parameter.

| Endpoint | Parameters | Returns |
|----------|-----------|---------|
| `track.search` | `q_track=`, `q_artist=`, `page_size=` | Track search results |
| `track.get` | `track_isrc=` or `commontrack_id=` | Full track object |
| `track.lyrics.get` | `track_isrc=` or `commontrack_id=` | Lyrics availability + snippet |
| `artist.search` | `q_artist=` | Artist search |
| `artist.get` | `artist_mbid=` or `artist_id=` | Artist detail |
| `album.get` | `album_id=` | Album detail |
| `album.tracks.get` | `album_id=` | Album tracklist |

Response envelope: `{ "message": { "header": { "status_code": 200 }, "body": {...} } }`

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Musixmatch API Key | Password | Yes | developer.musixmatch.com |
| `fetch_lyrics_snippet` | Fetch Lyrics Snippet | Boolean | No | Default: false |
| `include_translation` | Include Translation Data | Boolean | No | Default: false |

---

## Fields Populated

```
title, cast (artists), year, genres, rating,
musicbrainz_recording_id (via ISRC cross-ref),
metadata_json: { isrc, musixmatch_track_id, musixmatch_commontrack_id,
                 has_lyrics, has_subtitles, lyrics_copyright,
                 track_rating, num_favourite, primary_genres }
```

---

## Rate Limits

- Free tier: 2,000 req/day; commercial tiers available
- Respect `Retry-After` on HTTP 429
- Cache track metadata for 7 days

---

## Implementation Notes

- ISRC is the best lookup key — use `track.get` with `track_isrc=`
  when an ISRC is available from MusicBrainz or Discogs
- `commontrack_id` is Musixmatch's own stable integer ID — store in
  `media_external_ids` with source `musixmatch`
- Free tier returns only the first 30 % of lyrics plus a snippet;
  Chronicle does not display full lyrics, so snippets suffice
- `primary_genres.music_genre_list` contains structured genre objects
  — map to Chronicle's `genres` field
- MusicBrainz artist IDs are accepted by `artist.get` (`artist_mbid=`)
  which provides a reliable cross-reference with the MusicBrainz plugin
- `has_subtitles: true` indicates the track has synced/timed lyrics
  (LRC format) — a useful boolean to store in metadata

---

## Scaffold Location

```
Chronicle.Plugin.Musixmatch/
├── Chronicle.Plugin.Musixmatch.csproj
├── README.md
├── manifest.json
├── MusixmatchPlugin.cs
└── Models/
    ├── MxmTrack.cs
    └── MxmArtist.cs
```
