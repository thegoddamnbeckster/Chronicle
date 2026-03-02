# Chronicle.Plugin.ISRC — Design Document

**Plugin ID:** `chronicle.plugin.isrc`
**Version:** 1.0.0
**Media Types:** Music (`music`)
**Auth:** None (public ISRC search via IFPI portal)
**API:** ISRC Search — `https://www.isrcsearch.ifpi.org`

---

## Purpose

The [International Standard Recording Code](https://www.isrc.ifpi.org/)
(ISRC) is the international standard identifier for audio and music video
recordings (ISO 3901). Every commercial recording has a unique ISRC.
This plugin uses the IFPI's public ISRC search portal to resolve ISRCs to
track metadata — title, artist, label, and release year — providing a
reliable fallback identifier lookup that works across all other music
databases.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 8 | ISRC-to-metadata resolution |

---

## API Overview

The IFPI ISRC search is a public HTML portal:

| URL | Description |
|-----|-------------|
| `https://www.isrcsearch.ifpi.org/?q={isrc}` | ISRC lookup page |
| `https://www.isrcsearch.ifpi.org/api/v1/search?isrc={isrc}` | JSON API (unofficial) |

Response fields: `isrc`, `title`, `mainArtist`, `recordingYear`,
`labelName`, `duration`

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 1000 |

---

## Fields Populated

```
title, cast (artists), year,
isrc,
metadata_json: { isrc, label_name, recording_year, duration_ms }
```

---

## Rate Limits

- Public portal; minimum 1,000 ms between requests
- Cache ISRC lookups indefinitely — ISRCs are permanent identifiers

---

## Implementation Notes

- This plugin's role is **identifier resolution**, not primary metadata
  enrichment — use it when you have an ISRC but no other metadata
- ISRC format: `CC-XXX-YY-NNNNN` (country code, registrant, year, sequence)
  — validate format before querying
- ISRCs are the best cross-reference key between Spotify, Apple Music,
  Deezer, Tidal, MusicBrainz, and Musixmatch — always harvest ISRCs
  from those plugins and store in `media_external_ids` with source `isrc`
- A single recording may have multiple ISRCs if re-released or remixed;
  the earliest ISRC is the canonical one
- The ISRC search portal is rate-sensitive — do not bulk-query without
  already possessing the ISRC from another source
- This plugin is best invoked when Chronicle needs to confirm that two
  records from different sources represent the same recording

---

## Scaffold Location

```
Chronicle.Plugin.ISRC/
├── Chronicle.Plugin.ISRC.csproj
├── README.md
├── manifest.json
├── ISRCPlugin.cs
└── Models/
    └── IsrcRecord.cs
```
