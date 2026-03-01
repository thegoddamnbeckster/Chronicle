# Chronicle.Plugin.Jaxsta — Design Document

**Plugin ID:** `chronicle.plugin.jaxsta`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** API key (subscription — jaxsta.com)
**API:** Jaxsta REST API — `https://api.jaxsta.com`

---

## Purpose

[Jaxsta](https://jaxsta.com/) is the world's largest dedicated music credits
database, curated from official music industry sources (Universal, Sony, Warner,
Merlin). It provides verified songwriting credits, production credits, and
label information that is more accurate and comprehensive than crowd-sourced
alternatives. Ideal for enriching music entries with authoritative credits.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 4 | Track credits — songwriters, producers, engineers |
| `album` | 4 | Album-level credits |
| `artist` | 4 | Artist profile and credits |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Artist search | `GET /v1/artists?q={name}` |
| Artist detail | `GET /v1/artists/{id}` |
| Artist credits | `GET /v1/artists/{id}/credits` |
| Release search | `GET /v1/releases?q={title}&artist={name}` |
| Release detail | `GET /v1/releases/{id}` |
| Track detail | `GET /v1/tracks/{id}` |
| Track credits | `GET /v1/tracks/{id}/credits` |

All requests require `Authorization: Bearer {token}` header.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Jaxsta API Key | Password | Yes | From jaxsta.com developer program |
| `include_engineers` | Include Engineering Credits | Boolean | No | Default: false |
| `include_all_credits` | Include All Credit Types | Boolean | No | Default: true |

---

## Fields Populated

```
cast (performers), directors (producers),
metadata_json: { songwriters, producers, mixers, engineers,
                 mastering_engineers, labels, isrc_codes,
                 jaxsta_release_id, jaxsta_artist_id }
```

---

## Rate Limits

- Varies by subscription tier
- Jaxsta API is commercial; rate limits defined per contract
- Cache credits data aggressively — credits are immutable

---

## Implementation Notes

- Jaxsta's primary value is in music credits — who wrote, produced,
  mixed, and mastered each track — data that MusicBrainz partially
  covers but Jaxsta covers more authoritatively
- ISRC codes from Jaxsta can be used to cross-reference with Spotify,
  Apple Music, and other streaming platforms
- Credit roles to map: `Composer`, `Lyricist`, `Producer`, `Featuring`,
  `Performer`, `Mixer`, `Mastering Engineer`, `Recording Engineer`
- Store the Jaxsta release ID in `media_external_ids` with source `jaxsta`

---

## Scaffold Location

```
Chronicle.Plugin.Jaxsta/
├── Chronicle.Plugin.Jaxsta.csproj
├── README.md (this document)
├── manifest.json
├── JaxstaPlugin.cs
└── Models/
    ├── JaxstaArtist.cs
    ├── JaxstaRelease.cs
    └── JaxstaCredit.cs
```
