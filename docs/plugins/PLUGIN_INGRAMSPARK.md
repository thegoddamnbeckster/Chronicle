# Chronicle.Plugin.IngramSpark — Design Document

**Plugin ID:** `chronicle.plugin.ingramspark`
**Version:** 1.0.0
**Media Types:** Books (`book`)
**Auth:** IngramSpark publisher account credentials
**API:** Ingram Content Group API — `https://connect.ingramcontent.com`

---

## Purpose

[IngramSpark](https://www.ingramspark.com/) is the world's largest book
distributor and a major provider of independent book publishing services.
IngramSpark's catalogue (via Ingram Content Group's API) provides metadata
for millions of print and digital titles, including indie-published books
not found in consumer databases. This plugin targets users who are IngramSpark
publishers or distributors with API access.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `book` | 5 | Independent and trade book metadata |

---

## API Overview

Ingram Content Group provides an EDI and REST API for accredited publishers
and distributors.

| Operation | Endpoint |
|-----------|---------|
| Title search | `GET /v1/titles?isbn={isbn}` |
| Title detail | `GET /v1/titles/{isbn}` |
| Title availability | `GET /v1/titles/{isbn}/availability` |
| Publisher titles | `GET /v1/publisher/{id}/titles` |

All requests use OAuth 2.0 client credentials flow.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `client_id` | Ingram Client ID | Text | Yes | From Ingram developer portal |
| `client_secret` | Ingram Client Secret | Password | Yes | From Ingram developer portal |
| `publisher_id` | Publisher ID | Text | No | For publisher-scoped queries |

---

## Fields Populated

```
title, overview, year, genres (BISAC), cast (authors), publisher,
isbn_10, isbn_13, page_count, binding, list_price, language,
availability, ingram_id, territories, trim_size
```

---

## Rate Limits

- Defined per API contract
- Cache title data for 24 hours minimum

---

## Implementation Notes

- IngramSpark API primarily serves publishers tracking their own titles;
  general metadata lookup may require a broader Ingram distribution contract
- Availability data is particularly useful — Ingram supplies to 40,000+
  retailers; availability status reflects global distribution
- OAuth token should be refreshed using client credentials flow before expiry
- BISAC subject codes used — map to Chronicle genres

---

## Scaffold Location

```
Chronicle.Plugin.IngramSpark/
├── Chronicle.Plugin.IngramSpark.csproj
├── README.md (this document)
├── manifest.json
├── IngramSparkPlugin.cs
└── Models/
    └── IngramTitle.cs
```
