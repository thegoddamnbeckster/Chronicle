# Chronicle.Plugin.ListenBrainz — Design Document

**Plugin ID:** `chronicle.plugin.listenbrainz`
**Version:** 1.0.0
**Media Types:** Music (`music`), Artists (`artist`)
**Auth:** API token (free — listenbrainz.org)
**API:** ListenBrainz API v1 — `https://api.listenbrainz.org/1`

---

## Purpose

[ListenBrainz](https://listenbrainz.org/) is MetaBrainz Foundation's
open-source, privacy-respecting scrobbling service — a free alternative
to Last.fm. It stores listening history, generates personalised
recommendations, and provides community listening statistics for any track
with a MusicBrainz ID. This plugin both enriches music metadata with
community stats and imports a user's listening history.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 5 | Track listening stats |
| `artist` | 6 | Artist listening popularity |

---

## API Overview

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/user/{user}/listens` | GET | User's listen history |
| `/user/{user}/playing-now` | GET | Currently playing track |
| `/user/{user}/stats/artists` | GET | Top artists (week/month/all) |
| `/user/{user}/stats/releases` | GET | Top albums |
| `/user/{user}/stats/recordings` | GET | Top tracks |
| `/user/{user}/similar-users` | GET | Similar listeners |
| `/metadata/lookup` | GET | Lookup by artist+recording name |
| `/metadata/recording` | GET | Track metadata by MBID |
| `/stats/sitewide/artists` | GET | Global top artists |

Token header: `Authorization: Token {token}`

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `user_token` | ListenBrainz Token | Password | Yes | listenbrainz.org/profile/ |
| `username` | ListenBrainz Username | Text | Yes | For history import |
| `stats_period` | Stats Period | Dropdown | No | `week`, `month`, `year`, `all_time` |
| `import_history` | Import Listen History | Boolean | No | Default: false |

---

## Fields Populated

```
title, cast (artists), musicbrainz_recording_id,
metadata_json: { listen_count, global_listen_count,
                 listenbrainz_username, similar_users }
```

---

## Rate Limits

- 50 req/sec without token; higher with token
- History endpoint paginates at 100 listens per page

---

## Implementation Notes

- ListenBrainz uses MusicBrainz Recording IDs (MBIDs) as its primary
  identifiers — cross-reference perfectly with MusicBrainz plugin
- For scrobble import, implement `IImportProvider` alongside
  `IMetadataProvider`; use the `/listens` endpoint with timestamp pagination
- The `metadata/lookup` endpoint accepts artist name + recording name and
  returns the MBID — useful for fuzzy matching
- ListenBrainz's recommendation engine (`/user/{user}/recommendations/`) is
  a future enhancement for a "Discovery" feature in Chronicle
- Store the MBID from ListenBrainz responses in `media_external_ids` with
  source `musicbrainz`

---

## Scaffold Location

```
Chronicle.Plugin.ListenBrainz/
├── Chronicle.Plugin.ListenBrainz.csproj
├── README.md
├── manifest.json
├── ListenBrainzPlugin.cs
└── Models/
    ├── LbListen.cs
    └── LbUserStats.cs
```
