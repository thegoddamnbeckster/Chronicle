# Chronicle.Plugin.GoogleBooks

Metadata source plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) that
fetches book and audiobook metadata from the [Google Books API](https://developers.google.com/books).

**Plugin ID:** `chronicle.plugin.googlebooks`
**Version:** 1.0.0
**Media Types:** Books (`books`), Audiobooks (`audiobooks`)
**Auth:** API key (free — Google Cloud Console)
**API:** Google Books API v1 — `https://www.googleapis.com/books/v1`

---

## Table of Contents

- [Overview](#overview)
- [Supported Media Types](#supported-media-types)
- [Settings Schema](#settings-schema)
- [API Details](#api-details)
- [External ID Format](#external-id-format)
- [Fix Match Resolution](#fix-match-resolution)
- [Search Strategy](#search-strategy)
- [GetByIdAsync](#getbyidasync)
- [metadata_json Storage](#metadata_json-storage)
- [Rate Limiting](#rate-limiting)
- [Background Tasks](#background-tasks)
- [Repository Structure](#repository-structure)
- [Building & Packaging](#building--packaging)

---

## Overview

The [Google Books API](https://developers.google.com/books/docs/v1/using) provides access
to Google's bibliographic database, covering millions of books with consistently-structured
metadata including descriptions, publisher info, ISBNs, cover thumbnails, ratings, and
content previews.

A free API key is required (obtain via [Google Cloud Console](https://console.cloud.google.com/)
— no billing required for the free tier). The free tier allows 1,000 requests/day per key,
which is sufficient for a personal media server doing incremental enrichment.

Key strengths over Open Library:
- Better description coverage for recent and mainstream books
- Community ratings (`averageRating` / `ratingsCount`)
- Publisher, publish date, and page count more consistently present
- `intitle:` / `inauthor:` query operators for precise search

Key weaknesses:
- Cover images are thumbnails only (~128×192 px); no full-resolution covers
- Series data not available in the API
- 1,000 req/day hard quota on the free tier
- Volume IDs are edition-specific (not canonical across editions)
- API key required — users must create a Google Cloud project

---

## Supported Media Types

| Media Type | HierarchyLevel | Priority | Notes |
|------------|----------------|----------|-------|
| `books` | 0 | 3 | Lower priority than Open Library; use GB for richer descriptions |
| `audiobooks` | 0 | 3 | Same data model; note: no narrator info available via Google Books |

Priority 3 means it runs after Open Library (priority 2) when both are enabled. Both can
be active simultaneously — Chronicle shows both metadata boxes on the detail page.

---

## Settings Schema

| Key | Label | Type | Required | Default | Notes |
|-----|-------|------|----------|---------|-------|
| `ApiKey` | Google Books API Key | Password | **Yes** | — | From Google Cloud Console. Stored encrypted. |
| `MaxRetries` | Max Retries | Number | No | `3` | Per-item failure limit before `Exhausted` |
| `RateLimitMs` | Rate Limit (ms) | Number | No | `200` | ms between requests; practical safe rate ~5 req/sec |
| `MaxResults` | Max Results | Number | No | `10` | Max candidates per search (1–40) |

### Getting a Free API Key

1. Go to [console.cloud.google.com](https://console.cloud.google.com/)
2. Create a new project (or use an existing one)
3. Enable the **Books API** in the API Library
4. Go to Credentials → Create Credentials → API Key
5. (Optional but recommended) Restrict the key to the Books API only

---

## API Details

| Property | Value |
|----------|-------|
| Base URL | `https://www.googleapis.com/books/v1` |
| Protocol | REST/JSON over HTTPS |
| Authentication | `?key={ApiKey}` query parameter |
| Rate limit | ~1,000 req/day (free tier); no hard per-second limit (be polite) |
| Response format | JSON |

### Endpoint: Search Volumes

```
GET https://www.googleapis.com/books/v1/volumes
    ?q={query}
    &key={ApiKey}
    &maxResults={n}
    &printType=books
```

**Query operators:**
- `intitle:` — restrict to title field
- `inauthor:` — restrict to author
- `inpublisher:` — restrict to publisher
- `subject:` — restrict to subject/genre
- `isbn:` — search by ISBN-10 or ISBN-13
- `lccn:` — Library of Congress Control Number

Example: `q=intitle:"The+Name+of+the+Wind"+inauthor:"Patrick+Rothfuss"`

### Endpoint: Get Volume by ID

```
GET https://www.googleapis.com/books/v1/volumes/{volumeId}
    ?key={ApiKey}
```

### Volume Information Fields (volumeInfo)

```
title, subtitle, authors[], publisher, publishedDate (YYYY, YYYY-MM, or YYYY-MM-DD),
description (HTML may be present — strip tags),
industryIdentifiers[{type: ISBN_13|ISBN_10, identifier}],
pageCount, printType, categories[],
averageRating, ratingsCount, maturityRating,
imageLinks{smallThumbnail, thumbnail},
language, previewLink, infoLink, canonicalVolumeLink
```

> **Note:** `categories` is typically a single-element array with a slash-separated hierarchy
> (e.g., `"Fiction / Fantasy / Epic"`). Parse and split on `/` for individual genre tags.

> **Note:** `publishedDate` format varies — may be `"2007"`, `"2007-03"`, or `"2007-03-27"`.
> Parse flexibly; extract year only when full date unavailable.

---

## External ID Format

| Entity | Format | Example |
|--------|--------|---------|
| Volume (edition) | `google:{volumeId}` | `google:zyTCAlFPjgYC` |

Google Books volume IDs are edition-specific alphanumeric strings. They are NOT
canonical across different printings of the same work. Store as `google:{volumeId}` in
`media_external_ids` with source `google_books`.

---

## Fix Match Resolution

`fixMatchHint`: *Enter a Google Books URL (e.g. https://books.google.com/books?id=zyTCAlFPjgYC) or volume ID (e.g. google:zyTCAlFPjgYC), or an ISBN*

| Input | Resolution |
|-------|-----------|
| `https://books.google.com/books?id={volumeId}` | Extract `id` param → `GET /volumes/{id}` |
| `google:{volumeId}` | `GET /volumes/{volumeId}` |
| Bare alphanumeric (looks like volume ID) | `GET /volumes/{id}` |
| `isbn:{isbn13}` or 13-digit number | `GET /volumes?q=isbn:{isbn13}` |
| `isbn10:{isbn10}` or 10-digit string | `GET /volumes?q=isbn:{isbn10}` |

---

## Search Strategy

### Stage 1a — AltTitles with year, using `intitle:` + `inauthor:`

For each title in `context.AltTitles`:

1. `GET /volumes?q=intitle:"{title}"+inauthor:"{author}"&key=...&maxResults=10`
   (`inauthor:` omitted if `context.ParentName` is null)
   Title and author are phrase-quoted with `"`; spaces replaced with `+`
2. Score results (see table below); year compared at scoring time
3. Accept if top candidate scores ≥ 50 — stop

### Stage 1b — AltTitles without `inauthor:`, with year

For each AltTitle (removes author constraint which may be over-restrictive):

1. `GET /volumes?q=intitle:"{title}"&key=...`
2. Score with year

### Stage 1c — AltTitles without year

If stages 1a/1b failed:

1. Same queries, year excluded from scoring
2. Accept if score ≥ 50

If all stages yield zero candidates → `NotFound`.

### ISBN Short-Circuit

If file scanner has ISBN-13 or ISBN-10:

1. `GET /volumes?q=isbn:{isbn}&key=...`
2. ISBN match → confirmed result, threshold not applied

### Scoring Signals

| Signal | Score |
|--------|-------|
| Exact title match (normalised, lower) | +40 |
| Fuzzy title match (Levenshtein ≤ 20%) | +20 |
| Published year exact match | +20 |
| Published year ±1 | +10 |
| Year mismatch > 1 | −10 |
| Author name match (`context.ParentName`) | +15 |
| Category partial match | +5 |
| ISBN match | auto-accept |

Default acceptance threshold: **50**

---

## GetByIdAsync

| Input format | Behaviour |
|---|---|
| `google:{volumeId}` | `GET /volumes/{volumeId}` |
| `isbn:{isbn13}` | `GET /volumes?q=isbn:{isbn13}` → first result |
| `isbn10:{isbn10}` | `GET /volumes?q=isbn:{isbn10}` → first result |
| `https://books.google.com/books?id={id}` | Extract `id` param → `GET /volumes/{id}` |

---

## `metadata_json` Storage

All data stored under the full plugin ID key:

```json
{
  "chronicle.plugin.googlebooks": {
    "volume_id": "zyTCAlFPjgYC",
    "title": "The Name of the Wind",
    "subtitle": null,
    "description": "Told in Kvothe's own voice, this is the tale of the magically gifted young man who grows to be the most notorious wizard his world has ever seen.",
    "authors": ["Patrick Rothfuss"],
    "publisher": "DAW Books",
    "published_date": "2007-03-27",
    "page_count": 662,
    "language": "en",
    "print_type": "BOOK",
    "categories": ["Fiction", "Fantasy", "Epic Fantasy"],
    "average_rating": 4.5,
    "ratings_count": 12847,
    "maturity_rating": "NOT_MATURE",
    "isbn_10": "0756404738",
    "isbn_13": "9780756404734",
    "thumbnail_url": "https://books.google.com/books/content?id=zyTCAlFPjgYC&printsec=frontcover&img=1&zoom=1",
    "preview_link": "https://books.google.com/books?id=zyTCAlFPjgYC",
    "info_link": "https://play.google.com/store/books/details?id=zyTCAlFPjgYC",
    "canonical_volume_link": "https://play.google.com/store/books/details?id=zyTCAlFPjgYC"
  }
}
```

### First-Class Chronicle Field Mappings

| `metadata_json` field | Chronicle field |
|---|---|
| `title` | `media_items.name` |
| `published_date` (year extracted) | `media_items.year` |
| `thumbnail_url` | Poster image (note: thumbnail quality only) |
| `description` | PluginMetadataBox overview (strip HTML if present) |
| `authors[0]` | Primary creator display |
| `isbn_13` | `media_external_ids` |
| `average_rating` + `ratings_count` | Shown in plugin metadata box |

> **Cover quality note:** Google Books only provides thumbnail images (~128×192 px). If
> Open Library is also enabled for the same item, its covers (full resolution) take
> precedence for the poster. See plugin priority settings.

---

## Rate Limiting

`SemaphoreSlim(1,1)` + `Stopwatch` elapsed guard — same pattern as MusicBrainz.

- Default gap: 200ms (~5 req/sec)
- All search and GetById calls go through the limiter
- Image downloads do NOT count against the rate limit (separate CDN)
- Daily quota tracking: the plugin logs a warning when an HTTP 429 is received; on 429,
  back off for 60 seconds and retry once. If still 429, mark the item `Failed` (not
  `Exhausted`) so it retries the next day.

---

## Background Tasks

| Task | Schedule | Enabled by default |
|------|----------|-------------------|
| `fetch-missing-metadata` | Daily at 04:30 | Yes |
| `resync-all-metadata` | Weekly Sunday 03:30 | No |

(Offset by 30 minutes from Open Library to avoid both running simultaneously)

---

## Repository Structure

```
W:\Scripts\Chronicle.Plugin.GoogleBooks\
├── Chronicle.Plugin.GoogleBooks.csproj
├── manifest.json
├── GoogleBooksMetadataProvider.cs    # IMetadataProvider implementation
├── GoogleBooksClient.cs              # REST HTTP client + rate limiter + API key
├── GoogleBooksSearcher.cs            # SearchAsync cascade, AltTitles iteration, ISBN short-circuit
├── GoogleBooksModels.cs              # C# models for JSON deserialization (VolumeInfo etc.)
├── GoogleBooksMetadataMapper.cs      # GoogleBooksVolume → MediaMetadata + metadata_json
└── tests/
    ├── GoogleBooksSearcherTests.cs
    ├── GoogleBooksMetadataMapperTests.cs
    └── GoogleBooksClientTests.cs
```

---

## Building & Packaging

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>Chronicle.Plugin.GoogleBooks</AssemblyName>
    <RootNamespace>Chronicle.Plugin.GoogleBooks</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                      Private="false"
                      ExcludeAssets="runtime" />
    <ProjectReference Include="..\Chronicle\src\Chronicle.Core\Chronicle.Core.csproj"
                      Private="false"
                      ExcludeAssets="runtime" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Http" Version="9.0.3" />
    <PackageReference Include="System.Text.Json" Version="9.0.3" />
  </ItemGroup>
  <ItemGroup>
    <None Update="manifest.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

Deploy output to: `plugins/googlebooks/` under the Chronicle API publish directory.

---

## Acceptance Criteria

1. `google:zyTCAlFPjgYC` resolves via `GetByIdAsync` — all fields stored
2. `intitle:` + `inauthor:` search returns correct result at score ≥ 50
3. ISBN-13 in file scanner metadata bypasses search and resolves directly
4. Year-prefixed folder name matched via Stage 1c fallback
5. Book absent from Google Books → `NotFound`
6. HTTP 429 (daily quota exceeded) marks item `Failed` (not `Exhausted`) for next-day retry
7. `description` HTML tags stripped before storage
8. `publishedDate` parsed correctly whether `"2007"`, `"2007-03"`, or `"2007-03-27"`

---

## Out of Scope (v1.0)

- OAuth for accessing private bookshelves
- Series data (not available in the Google Books API)
- Full-resolution cover art (Google Books only provides thumbnails)
- Syncing Chronicle events back to Google Books

---

*Chronicle.Plugin.GoogleBooks is an independent community plugin and is not affiliated with
or endorsed by Google.*
