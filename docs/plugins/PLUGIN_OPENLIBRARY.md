# Chronicle.Plugin.OpenLibrary — Design Document

**Plugin ID:** `chronicle.plugin.openlibrary`
**Version:** 1.0.0
**Media Types:** Books (`book`)
**Auth:** None (fully public API)
**API:** Open Library REST API — `https://openlibrary.org`

---

## Purpose

[Open Library](https://openlibrary.org/) is an Internet Archive project
providing free, open access to book metadata for millions of titles. It offers
a completely public API with no API key required, making it an excellent
no-friction primary or fallback book metadata source. Open Library also
provides book cover images through its Covers API.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `book` | 2 | Comprehensive free book metadata |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Search | `GET /search.json?q={query}&fields=*&limit={n}` |
| Book by ISBN | `GET /isbn/{isbn}.json` |
| Work by ID | `GET /works/{olid}.json` |
| Edition by ID | `GET /books/{olid}.json` |
| Author by ID | `GET /authors/{olid}.json` |
| Author works | `GET /authors/{olid}/works.json` |
| Cover image | `GET https://covers.openlibrary.org/b/isbn/{isbn}-L.jpg` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `cover_size` | Cover Image Size | Dropdown | No | `S`, `M`, `L` — default: `L` |
| `include_editions` | Fetch All Editions | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview (first_sentence / description), year, genres (subjects),
cast (authors), publisher, isbn_10, isbn_13, page_count, language,
poster_url (cover), openlibrary_work_id, openlibrary_edition_id,
dewey_decimal, lc_classification, number_of_editions
```

---

## Rate Limits

- No official rate limit; be polite (1 req/sec)
- Cover images are served from a CDN — no rate limiting known
- The full text search endpoint can be slow (5–15 s) — increase timeout

---

## Implementation Notes

- Open Library has two record types: **Works** (canonical) and **Editions**
  (specific printings). Prefer Works for metadata; use Editions for
  physical details (pages, publisher, ISBN)
- Cover image URL pattern:
  `https://covers.openlibrary.org/b/{key}/{value}-{size}.jpg`
  where key is `isbn`, `olid`, `lccn`, etc.
- Work descriptions can be a plain string OR `{ type, value }` object —
  handle both cases
- `first_sentence` is often more useful than `description` for overviews
- The `subjects` array is very large on popular books — limit to top 5
- Store the Open Library Work ID (e.g., `/works/OL45883W`) with
  source `openlibrary` in `media_external_ids`

---

## Scaffold Location

```
Chronicle.Plugin.OpenLibrary/
├── Chronicle.Plugin.OpenLibrary.csproj
├── README.md (this document)
├── manifest.json
├── OpenLibraryPlugin.cs
└── Models/
    ├── OpenLibraryWork.cs
    ├── OpenLibraryEdition.cs
    └── OpenLibraryAuthor.cs
```
