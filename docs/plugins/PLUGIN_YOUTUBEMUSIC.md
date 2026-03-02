# Chronicle.Plugin.YouTubeMusic — Design Document

**Plugin ID:** `chronicle.plugin.youtubemusic`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** YouTube Data API v3 key (free quota)
**API:** YouTube Data API v3 — `https://www.googleapis.com/youtube/v3`

---

## Purpose

[YouTube Music](https://music.youtube.com/) is Google's music streaming
service built on YouTube's vast catalogue. It contains official releases,
music videos, live performances, remixes, and user-uploaded content not
available elsewhere. The YouTube Data API provides access to metadata for
music videos and official artist channels. This plugin is especially
valuable for music videos, unofficial releases, and live recordings.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 6 | Music video and official track metadata |
| `album` | 7 | Official artist album playlists |
| `artist` | 6 | Artist channel data |

---

## API Overview

Base URL: `https://www.googleapis.com/youtube/v3`
Auth: `?key={api_key}` query parameter

| Endpoint | Parameters | Returns |
|----------|-----------|---------|
| `GET /search` | `q=`, `type=video,channel`, `videoCategoryId=10` | Music video search |
| `GET /videos` | `id=`, `part=snippet,contentDetails,statistics` | Video detail |
| `GET /channels` | `id=` or `forHandle=`, `part=snippet,statistics` | Channel (artist) detail |
| `GET /playlists` | `channelId=`, `part=snippet` | Channel playlists (albums) |
| `GET /playlistItems` | `playlistId=`, `part=snippet` | Playlist tracks |

`videoCategoryId=10` filters to Music category.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | YouTube Data API Key | Password | Yes | console.cloud.google.com |
| `include_statistics` | Include View Counts | Boolean | No | Default: true |
| `preferred_region` | Region Code | Text | No | Default: `US` |

---

## Fields Populated

```
title, overview (video description), year, cast (artists),
poster_url (thumbnail), duration, youtube_video_id,
youtube_channel_id, youtube_url,
metadata_json: { view_count, like_count, comment_count,
                 channel_title, tags, category_id,
                 definition, is_official_video }
```

---

## Rate Limits

- Free quota: 10,000 units/day; `videos.list` costs 1 unit/request
- `search.list` costs 100 units — use sparingly; prefer direct ID lookups
- Cache video metadata for 24 hours; statistics for 1 hour

---

## Implementation Notes

- YouTube Music does not have a dedicated metadata API — use the
  YouTube Data API v3 with music-specific filters
- The `snippet.categoryId == "10"` filter and `type=video` + `videoCategoryId`
  in search narrow results to music content
- YouTube video IDs (11-char Base64) are stable — store in
  `media_external_ids` with source `youtube`
- For official music videos, the `snippet.channelId` corresponds to the
  artist's official YouTube channel
- `contentDetails.duration` uses ISO 8601 duration format (e.g. `PT3M45S`)
  — parse to seconds for Chronicle's `duration` field
- Thumbnails: prefer `maxres.url` (1280×720); fall back to `high.url` (480p)
- YouTube Music's "Official Track" / "Auto-generated" distinction can be
  inferred from channel verification status (`status.privacyStatus`)

---

## Scaffold Location

```
Chronicle.Plugin.YouTubeMusic/
├── Chronicle.Plugin.YouTubeMusic.csproj
├── README.md
├── manifest.json
├── YouTubeMusicPlugin.cs
└── Models/
    ├── YtVideo.cs
    └── YtChannel.cs
```
