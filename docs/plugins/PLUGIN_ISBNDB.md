# Chronicle.Plugin.ISBNdb — Design Document

**Plugin ID:** `chronicle.plugin.isbndb`
**Version:** 1.0.0
**Media Types:** Books (`book`)
**Auth:** API key (free tier available — isbndb.com)
**API:** ISBNdb REST API v2 — `https://api2.isbndb.com`

---

## Purpose

[ISBNdb](https://isbndb.com/) is a comprehensive database of books indexed by
ISBN. It aggregates data from publishers, libraries, and booksellers to provide
accurate bibliographic metadata for books, e-books, and audiobooks. ISBN lookup
is the canonical way to identify book editions — this plugin makes ISBNdb the
primary book metadata source for Chronicle.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `book` | 1 | Full book metadata by ISBN or title search |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Search books | `GET /books/{query}` |
| Book by ISBN | `GET /book/{isbn}` |
| Author search | `GET /author/{name}` |
| Author books | `GET /author/{name}?page={n}` |
| Publisher search | `GET /publisher/{name}` |
| Subject search | `GET /subject/{subject}` |

All requests require `Authorization: {api_key}` header.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | ISBNdb API Key | Password | Yes | isbndb.com/register |
| `language` | Language Filter | Dropdown | No | Default: `en` |
| `edition_preference` | Edition Preference | Dropdown | No | `newest`, `oldest`, `any` |

---

## Fields Populated

```
title, overview (synopsis), year (publish_date), genres (subjects),
cast (authors), publisher, isbn_10, isbn_13, page_count,
language, binding (hardcover/paperback/etc.), edition,
poster_url (cover image), dimensions, weight
```

---

## Rate Limits

- Free tier: 1 req/sec, 1,000 req/day
- Premium: higher limits
- Cache ISBN lookups indefinitely — ISBN metadata rarely changes

---

## Implementation Notes

- ISBN-13 is the preferred identifier — always store in
  `media_external_ids` with source `isbn`
- ISBNdb cover images are from Amazon's CDN — they may be removed
  without notice; prefer Open Library for images if ISBNdb's are gone
- The `subjects` array maps directly to `genres` for books
- `binding` values: `Hardcover`, `Paperback`, `Mass Market Paperback`,
  `E-Book`, `Audiobook`, `Board Book`
- `publish_date` format varies (year only vs full date) — parse flexibly

---

## Scaffold Location

```
Chronicle.Plugin.ISBNdb/
├── Chronicle.Plugin.ISBNdb.csproj
├── README.md (this document)
├── manifest.json
├── ISBNdbPlugin.cs
└── Models/
    └── ISBNdbBook.cs
```
