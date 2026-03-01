# Chronicle.Plugin.Bowker — Design Document

**Plugin ID:** `chronicle.plugin.bowker`
**Version:** 1.0.0
**Media Types:** Books (`book`)
**Auth:** API key (commercial — Bowker / ProQuest)
**API:** Bowker / Books In Print API — contact Bowker for endpoint details

---

## Purpose

[Bowker](https://www.bowker.com/) is the official ISBN agency for the United
States and a leading provider of bibliographic data through its Books In Print
database. Bowker data is authoritative for publisher metadata, print/e-book
availability, and US market bibliographic records. This plugin targets Bowker's
data API for institutional and professional Chronicle users.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `book` | 3 | US market authoritative bibliographic data |

---

## API Overview

Bowker's APIs are commercial and require a contract. The plugin targets the
**Books In Print** data API which provides:

| Operation | Description |
|-----------|------------|
| ISBN lookup | Full bibliographic record by ISBN-13 |
| Title search | Search by title, author, subject, publisher |
| Publisher catalogue | List of titles by publisher |
| New releases | Recently published / upcoming titles |
| BISAC subjects | Books In Print subject classification |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Bowker API Key | Password | Yes | Commercial subscription |
| `api_endpoint` | API Base URL | Url | Yes | Provided by Bowker |
| `market` | Market | Dropdown | No | `US`, `UK`, `Global` |

---

## Fields Populated

```
title, overview, year, genres (BISAC subjects), cast (authors),
publisher, isbn_10, isbn_13, page_count, language, binding,
list_price, availability_status, bowker_id, pub_date,
series_name, edition_number
```

---

## Rate Limits

- Defined per commercial contract
- Cache ISBN lookups indefinitely — bibliographic data is stable

---

## Implementation Notes

- Bowker's BISAC subject codes are the industry standard for book
  categorisation — map these to Chronicle genres
- Availability status: `Active`, `Out of Print`, `Forthcoming`
- This plugin is primarily for institutional/professional users with
  Bowker subscriptions; most home users should use ISBNdb or Open Library
- API documentation provided under commercial agreement

---

## Scaffold Location

```
Chronicle.Plugin.Bowker/
├── Chronicle.Plugin.Bowker.csproj
├── README.md (this document)
├── manifest.json
├── BowkerPlugin.cs
└── Models/
    └── BowkerTitle.cs
```
