# Chronicle.Plugin.BarnesAndNoble — Design Document

**Plugin ID:** `chronicle.plugin.barnesandnoble`
**Version:** 1.0.0
**Media Types:** Books (`book`)
**Auth:** None (scraping) or B&N affiliate API key if available
**API:** Barnes & Noble product API / web scraping — `https://www.barnesandnoble.com`

---

## Purpose

[Barnes & Noble](https://www.barnesandnoble.com/) is the largest retail
bookseller in the United States. This plugin fetches book metadata including
descriptions, pricing, author information, and B&N editorial content. B&N
metadata is particularly useful for physical book availability data and
US market pricing.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `book` | 5 | US retail book metadata and pricing |

---

## API Overview

Barnes & Noble does not publish a general-purpose developer API. This plugin
uses one of the following approaches:

**Option A — Affiliate API (preferred, if enrolled):**
B&N's affiliate program provides product data feeds and API access.
Endpoint and format are provided upon affiliate approval.

**Option B — Structured web extraction:**
Barnes & Noble's product pages contain JSON-LD structured data
(`application/ld+json`) with `Book` schema.org markup:

```json
{
  "@type": "Book",
  "name": "...",
  "author": { "@type": "Person", "name": "..." },
  "isbn": "...",
  "description": "...",
  "image": "...",
  "offers": { "price": "...", "availability": "..." }
}
```

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `affiliate_key` | B&N Affiliate API Key | Password | No | Leave empty for web extraction |
| `user_agent` | User Agent | Text | No | Browser UA for scraping |
| `include_pricing` | Include Pricing | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview, year, genres, cast (authors), publisher,
isbn, poster_url, list_price, member_price, availability,
bn_ean, edition, page_count, language
```

---

## Rate Limits

- Web extraction: 1 req/5 sec; respect `robots.txt`
- Affiliate API: per contract terms
- Cache book data for 24 hours

---

## Implementation Notes

- Barnes & Noble EAN (European Article Number) is functionally the
  same as ISBN-13 for books — store as `isbn_13`
- JSON-LD extraction is the recommended scraping approach — more stable
  than HTML structure parsing
- B&N's "Member Price" is the B&N Membership discount price; store
  alongside `list_price` in `metadata_json`
- Nook (e-book) editions have separate product pages; the plugin should
  detect and handle both print and Nook editions
- User-Agent must resemble a real browser to avoid 403 responses

---

## Scaffold Location

```
Chronicle.Plugin.BarnesAndNoble/
├── Chronicle.Plugin.BarnesAndNoble.csproj
├── README.md (this document)
├── manifest.json
├── BarnesAndNoblePlugin.cs
└── Models/
    └── BnBook.cs
```
