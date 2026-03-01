# Chronicle.Plugin.WorldCat — Design Document

**Plugin ID:** `chronicle.plugin.worldcat`
**Version:** 1.0.0
**Media Types:** Books (`book`), Music (`music`), Movies (`movie`)
**Auth:** OCLC WSKey or API key (institution or developer program)
**API:** OCLC WorldCat Search API v2 — `https://americas.discovery.api.oclc.org/worldcat/v2`

---

## Purpose

[WorldCat](https://www.worldcat.org/) is the world's largest library catalogue,
aggregating holdings from 10,000+ libraries in 100+ countries via OCLC.
It is the definitive source for OCLC numbers (OCNs) — the library world's
canonical item identifiers — and provides bibliographic metadata for books,
music, films, maps, and more through its Search API v2.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `book` | 3 | Comprehensive library bibliographic data |
| `music` | 6 | Audio recordings held in libraries |
| `movie` | 6 | Films held in libraries |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Search bibliographic records | `GET /bibs?q={query}` |
| Record by OCLC Number | `GET /bibs/{oclc_number}` |
| Search by ISBN | `GET /bibs?q=ISBN:{isbn}` |
| Search by ISSN | `GET /bibs?q=ISSN:{issn}` |
| Holdings count | `GET /bibs-holdings/{oclc_number}` |

All requests require `Authorization: Bearer {token}` via OCLC's
OAuth 2.0 client credentials flow.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `client_id` | OCLC Client ID | Text | Yes | From OCLC developer program |
| `client_secret` | OCLC Client Secret | Password | Yes | From OCLC developer program |
| `language` | Preferred Language | Dropdown | No | Default: `en` |
| `include_holdings` | Fetch Holdings Count | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview, year, genres, cast (authors), publisher,
isbn, oclc_number, dewey_decimal, lc_classification,
language, edition, format, holding_library_count,
worldcat_url
```

---

## Rate Limits

- OCLC API limits vary by WSKey tier; developer tier: ~50,000 req/day
- Cache OCLC lookups for 30 days — library catalogue data is stable

---

## Implementation Notes

- OCLC Numbers (OCNs) are the library world's canonical identifiers;
  store in `media_external_ids` with source `worldcat`
- `holding_library_count` is a useful metric — widely-held items are
  more important/canonical
- The WorldCat Search API v2 returns MARC 21 / Dublin Core / JSON-LD
  structured data — prefer JSON-LD responses for easier parsing
- The API scope is `wcapi` for the client credentials grant
- WorldCat is particularly good for academic, technical, and obscure books
  that commercial databases miss

---

## Scaffold Location

```
Chronicle.Plugin.WorldCat/
├── Chronicle.Plugin.WorldCat.csproj
├── README.md (this document)
├── manifest.json
├── WorldCatPlugin.cs
└── Models/
    └── WorldCatBib.cs
```
