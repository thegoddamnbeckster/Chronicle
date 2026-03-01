# Chronicle.Plugin.TheAudioDB — Design Document

**Plugin ID:** `chronicle.plugin.theaudiodb`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** API key (free tier: `2` for testing; paid for production)
**API:** TheAudioDB REST API v1 — `https://www.theaudiodb.com/api/v1/json`

---

## Purpose

[TheAudioDB](https://www.theaudiodb.com/) is a free community music metadata
database providing artist biographies, album artwork, music videos, genre
information, and audio file metadata. It is designed specifically for media
centre software (Kodi, etc.) and offers high-quality artwork alongside
structured metadata.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 2 | Artist and track metadata |
| `album` | 2 | Full album detail with artwork |
| `artist` | 2 | Artist biography and discography |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Search artist | `GET /{key}/search.php?s={artist_name}` |
| Artist by ID | `GET /{key}/artist.php?i={tadb_id}` |
| Albums by artist | `GET /{key}/album.php?i={tadb_id}` |
| Album by ID | `GET /{key}/album.php?m={tadb_album_id}` |
| Tracks on album | `GET /{key}/track.php?m={tadb_album_id}` |
| Music videos | `GET /{key}/mvid.php?i={tadb_id}` |
| Discography | `GET /{key}/discography.php?s={artist_name}` |
| Artist by MusicBrainz ID | `GET /{key}/artist-mb.php?i={mbid}` |
| Album by MusicBrainz ID | `GET /{key}/album-mb.php?i={mbid}` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | TheAudioDB API Key | Password | Yes | Use `2` for free testing tier |
| `language` | Language | Dropdown | No | `en`, `de`, `fr`, `es`, etc. — default: `en` |
| `include_music_videos` | Fetch Music Videos | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview (biography), year, genres, poster_url (album art),
backdrop_url (artist fanart), label, country, rating,
tadb_artist_id, tadb_album_id, musicbrainz_artist_id,
musicbrainz_album_id, tracklist, music_video_urls
```

---

## Rate Limits

- Free tier (`2`): limited to personal/non-commercial use, ~2 req/sec
- Paid tier: higher limits — check theaudiodb.com/forum
- Cache responses: artist/album data rarely changes

---

## Implementation Notes

- TheAudioDB uses its own integer IDs but also exposes MusicBrainz IDs —
  use MusicBrainz IDs as the cross-reference standard; store both
- Artwork URLs point to images.theaudiodb.com — high-quality album art
  available in multiple sizes (append `/preview` for smaller thumbnails)
- Biography fields are available in multiple languages via the `strBio{Lang}`
  pattern (e.g., `strBioEN`, `strBioDE`)
- Genre data from TheAudioDB tends to be broader than Discogs — useful as
  a complementary source
- Store TADB artist ID with source `theaudiodb` in `media_external_ids`

---

## Scaffold Location

```
Chronicle.Plugin.TheAudioDB/
├── Chronicle.Plugin.TheAudioDB.csproj
├── README.md (this document)
├── manifest.json
├── TheAudioDBPlugin.cs
└── Models/
    ├── TADBArtist.cs
    ├── TADBAlbum.cs
    └── TADBTrack.cs
```
