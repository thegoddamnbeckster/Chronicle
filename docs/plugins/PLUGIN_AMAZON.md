# Chronicle.Plugin.Amazon — Design Document

**Plugin ID:** `chronicle.plugin.amazon`
**Version:** 1.0.0
**Media Types:** Books (`book`)
**Auth:** Amazon Product Advertising API 5.0 — Associate tag + key + secret
**API:** Amazon PA-API 5.0 — `webservices.amazon.com`

---

## Purpose

Amazon's product catalogue is among the most comprehensive for books, with
rich metadata including editorial reviews, customer ratings, rankings, and
pricing data. This plugin uses the **Product Advertising API 5.0** (PA-API)
to search and fetch book metadata from Amazon. PA-API requires participation
in Amazon's Associates affiliate programme.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `book` | 4 | Books, e-books, audiobooks |

---

## API Overview

PA-API 5.0 uses a signed HTTP request scheme (AWS Signature v4).

| Operation | Request Body Field |
|-----------|------------------|
| Search by keyword | `SearchItems` with `Keywords`, `SearchIndex: Books` |
| Lookup by ASIN | `GetItems` with `ItemIds: [asin]` |
| Lookup by ISBN | `SearchItems` with `Keywords: isbn:{isbn}` |

Key resource response groups:
- `ItemInfo` — title, contributors, classification, product info
- `Images` — cover images in multiple sizes
- `Offers` — pricing (requires Associate tag)
- `CustomerReviews` — rating and review count

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `access_key` | Amazon Access Key | Text | Yes | AWS/PA-API credentials |
| `secret_key` | Amazon Secret Key | Password | Yes | AWS/PA-API credentials |
| `associate_tag` | Associates Tag | Text | Yes | Your Amazon affiliate tag |
| `marketplace` | Marketplace | Dropdown | No | `www.amazon.com`, `www.amazon.co.uk`, etc. |

---

## Fields Populated

```
title, overview (editorial_review), year, genres (browse_nodes),
cast (authors), publisher, isbn, asin, page_count, language,
poster_url (image), list_price, amazon_rating, amazon_review_count,
amazon_url, best_sellers_rank
```

---

## Rate Limits

- PA-API: 1 req/sec by default; up to 8,640 req/day
- Rate limits increase with Associates programme traffic
- Cache results for 24 hours minimum (PA-API TOS requirement)

---

## Implementation Notes

- PA-API requires an active Associates account with qualifying sales;
  this limits the user base but Amazon provides the most thorough
  English-language book metadata
- ASIN (Amazon Standard Identification Number) is Amazon's internal ID;
  for books it typically matches the ISBN-10 — store as `asin` in
  `media_external_ids`
- Editorial reviews (publisher blurbs, author bios) are available
  under `ItemInfo.ContentInfo.Edition`
- The API requires a unique `PartnerTag` (Associates tag) in every request
- Responses are JSON via HTTP POST with `Content-Type: application/json`
  and `X-Amz-Target: com.amazon.paapi5.v1.ProductAdvertisingAPIv1.SearchItems`

---

## Scaffold Location

```
Chronicle.Plugin.Amazon/
├── Chronicle.Plugin.Amazon.csproj
├── README.md (this document)
├── manifest.json
├── AmazonPlugin.cs
└── Models/
    ├── PaApiSearchResult.cs
    └── PaApiItem.cs
```
