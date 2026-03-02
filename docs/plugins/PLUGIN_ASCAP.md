# Chronicle.Plugin.ASCAP — Design Document

**Plugin ID:** `chronicle.plugin.ascap`
**Version:** 1.0.0
**Media Types:** Music (`music`), Artists (`artist`)
**Auth:** None (public ACE search — no API key required)
**API:** ASCAP ACE Title Search — `https://www.ascap.com/repertory`

---

## Purpose

[ASCAP](https://www.ascap.com/) (American Society of Composers, Authors and
Publishers) is one of the three major US performing rights organisations
(PROs). Its ACE (ASCAP Clearinghouse Express) database is the authoritative
US source for music publishing credits — songwriter, composer, and music
publisher information. This plugin enriches Chronicle music entries with
verified songwriting credits and PRO registration data.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 7 | Songwriting credits and publisher data |
| `artist` | 8 | Songwriter/composer PRO registration |

---

## API Overview

ASCAP's ACE title search is accessible as a public JSON endpoint:

| Endpoint | Description |
|----------|-------------|
| `GET https://www.ascap.com/repertory#ace/search/title/{title}` | Title search page |
| `GET https://repertory.ascap.com/ACETitle` | JSON search API (unofficial) |

Unofficial JSON endpoint:
```
GET https://repertory.ascap.com/ACETitle?query={title}&rowstart=0&rowend=10
```
Returns: `{ results: [{ titleId, title, performerName, writers: [...], publishers: [...] }] }`

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 1500 |
| `include_publishers` | Fetch Publisher Data | Boolean | No | Default: true |

---

## Fields Populated

```
title, cast (writers + publishers),
ascap_title_id, ascap_url,
metadata_json: { iswc, writers: [{ name, role, ipi }],
                 publishers: [{ name, ipi }],
                 ascap_work_id }
```

---

## Rate Limits

- Public endpoint; minimum 1,500 ms between requests
- Cache songwriting credits for 30 days — PRO registrations are stable

---

## Implementation Notes

- ASCAP's primary value is **verified songwriter and publisher credits**
  — these supplement performer credits from MusicBrainz/Discogs
- ISWC (International Standard Musical Work Code) may be present in
  results — store in `media_external_ids` with source `iswc`
- IPI (Interested Parties Information) numbers uniquely identify
  writers and publishers in the rights management ecosystem
- The ACE API is unofficial and may change without notice — build with
  resilient HTML fallback parsing
- Cross-reference with BMI and SESAC plugins to handle songs registered
  with different PROs (a song can only be registered with one PRO)
- Writers list often includes separate credits for music vs lyrics
  (role: `WRITER`, `COMPOSER`, `LYRICIST`) — map to Chronicle cast roles

---

## Scaffold Location

```
Chronicle.Plugin.ASCAP/
├── Chronicle.Plugin.ASCAP.csproj
├── README.md
├── manifest.json
├── ASCAPPlugin.cs
└── Models/
    └── AscapWork.cs
```
