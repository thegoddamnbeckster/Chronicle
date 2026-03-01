# Chronicle.Plugin.GoogleBooks — Design Document

**Plugin ID:** `chronicle.plugin.googlebooks`
**Version:** 1.0.0
**Media Types:** Books (`book`)
**Auth:** API key (free — Google Cloud Console)
**API:** Google Books API v1 — `https://www.googleapis.com/books/v1`

---

## Purpose

[Google Books](https://books.google.com/) provides extensive bibliographic
metadata combined with Google's search quality. The API covers millions of
books with consistent, well-structured metadata and high-quality cover images.
The free tier (with API key) is generous enough for personal use.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `book` | 2 | Books and e-books |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Search volumes | `GET /volumes?q={query}&key={key}` |
| Volume by ID | `GET /volumes/{volume_id}?key={key}` |
| Search by ISBN | `GET /volumes?q=isbn:{isbn}&key={key}` |
| Search by ISSN | `GET /volumes?q=issn:{issn}&key={key}` |

Query operators: `intitle:`, `inauthor:`, `inpublisher:`, `subject:`,
`isbn:`, `lccn:`, `oclc:`

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Google API Key | Password | Yes | Google Cloud Console |
| `language_restrict` | Language | Dropdown | No | ISO 639-1 code |
| `print_type` | Print Type | Dropdown | No | `all`, `books`, `magazines` |
| `max_results` | Max Results | Number | No | Default: 10, max: 40 |

---

## Fields Populated

```
title, overview (description), year (publishedDate), genres (categories),
cast (authors), publisher, isbn_10, isbn_13, page_count, language,
poster_url (thumbnail), google_books_id, preview_link,
info_link, content_version, maturity_rating
```

---

## Rate Limits

- 1,000 req/day without key; ~1,000,000 req/day with key
- No per-second limit documented; add 200 ms delays to be safe
- Thumbnail images: served from Google CDN, no rate limit

---

## Implementation Notes

- Cover thumbnails from the API are small (128px wide); for higher
  resolution, modify the URL: replace `zoom=1` with `zoom=0` or
  remove the `&zoom=` parameter entirely
- `publishedDate` format varies: may be `YYYY`, `YYYY-MM`, or
  `YYYY-MM-DD` — parse flexibly
- The `categories` array is Google's own taxonomy — map to Chronicle
  genres; first category is usually the most specific
- `volumeInfo.industryIdentifiers` contains both ISBN-10 and ISBN-13
- `maturityRating`: `NOT_MATURE` or `MATURE`
- Store Google Books volume ID in `media_external_ids` with source
  `google_books`

---

## Scaffold Location

```
Chronicle.Plugin.GoogleBooks/
├── Chronicle.Plugin.GoogleBooks.csproj
├── README.md (this document)
├── manifest.json
├── GoogleBooksPlugin.cs
└── Models/
    ├── GoogleBooksVolume.cs
    └── GoogleBooksVolumeInfo.cs
```
