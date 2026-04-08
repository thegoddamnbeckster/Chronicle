# Hardcover Plugin Design

**Date:** 2026-04-07
**Status:** Draft
**Repo:** `W:\Scripts\Chronicle.Plugin.Hardcover\` (to be created)
**Website:** https://hardcover.app/

---

## Overview

Hardcover is a social book-tracking platform with a GraphQL API. The Chronicle Hardcover plugin fetches rich book and audiobook metadata — title, authors, narrators, publisher, publication date, description, genres, series position, cover art, ISBN — for items in Chronicle's `books` and `audiobooks` media types.

This is a standalone plugin project following the same structure as `Chronicle.Plugin.MusicBrainz` and `Chronicle.Plugin.TMDB`. A personal API token is required (generated from https://hardcover.app/account/api).

---

## Plugin Identity

```
plugin_id:   chronicle.plugin.hardcover
name:        Hardcover
version:     1.0.0
author:      Chronicle Contributors
entry_type:  Chronicle.Plugin.Hardcover.HardcoverMetadataProvider
fixMatchHint: Enter a Hardcover book URL (e.g. https://hardcover.app/books/the-name-of-the-wind) or a book ID (e.g. book:75984)
```

---

## Supported Media Types

Books and audiobooks are modelled as flat **HierarchyLevel 0** items. Series membership is stored as metadata (series name + numeric position) rather than a parent-child hierarchy — this matches how Hardcover itself models data and avoids creating artificial hierarchy nodes for series.

| Media Type | HierarchyLevel | Notes |
|------------|----------------|-------|
| `books` | 0 | Any book — novel, non-fiction, graphic novel, etc. |
| `audiobooks` | 0 | Audiobooks (narrators stored in metadata) |

> **Future extension:** A separate HierarchyLevel-0 `book_series` media type could represent a series container (e.g., "The Kingkiller Chronicle") with individual books as Level 1 children. This is out of scope for v1.0; individual books work well standalone.

---

## Settings Schema

| Key | Type | Required | Default | Notes |
|-----|------|----------|---------|-------|
| `ApiToken` | Password | **Yes** | — | Personal access token from hardcover.app/account/api. Stored encrypted via `IPluginSettingsProtector`. |
| `MaxRetries` | Number | No | `3` | Per-item failure limit before `Exhausted` |
| `RateLimitMs` | Number | No | `1200` | ms between API calls; Hardcover allows ~50 req/min (1 req/1200ms) |

---

## API Details

| Property | Value |
|----------|-------|
| Endpoint | `https://api.hardcover.app/v1/graphql` |
| Protocol | GraphQL over HTTPS (POST) |
| Authentication | `Authorization: Bearer {ApiToken}` |
| Rate limit | ~50 requests/minute (authenticated) |
| Response format | JSON (`application/json`) |

### GraphQL — Search Books

```graphql
query SearchBooks($query: String!, $limit: Int) {
  search(query: $query, query_type: "Book", page: 1, per_page: $limit) {
    results {
      found
      hits {
        document {
          id
          title
          slug
          release_year
          contributions {
            author { id name }
          }
          image { url }
          book_series {
            position
            series { id name }
          }
        }
      }
    }
  }
}
```

### GraphQL — Get Book by ID

```graphql
query GetBook($id: Int!) {
  books_by_pk(id: $id) {
    id
    title
    slug
    description
    release_year
    release_date
    pages
    language
    isbn_10
    isbn_13
    contributions {
      contribution
      author {
        id
        name
        slug
        bio
        image { url }
      }
    }
    book_series {
      position
      series { id name slug }
    }
    genres {
      genre { id name }
    }
    moods {
      mood { id name }
    }
    taggings {
      tag { tag }
    }
    image { url color }
    default_physical_edition {
      id
      isbn_10
      isbn_13
      pages
      publisher { name }
      release_date
      release_year
      format
      audio_seconds
      narrations {
        narrator { name }
      }
    }
    editions_count
    users_count
    ratings_count
    rating
  }
}
```

### GraphQL — Get Book by Slug (used for Fix Match URL parsing)

```graphql
query GetBookBySlug($slug: String!) {
  books(where: { slug: { _eq: $slug } }, limit: 1) {
    id
    title
    slug
  }
}
```

---

## External ID Formats

| Entity | Format | Example |
|--------|--------|---------|
| Book | `book:{hardcoverId}` | `book:75984` |
| Author | `author:{hardcoverId}` | `author:12345` |

The `hardcoverId` is the integer `id` field from the GraphQL response. Slugs are human-readable but are used only for Fix Match URL parsing — the canonical external ID always uses the integer.

### Fix Match ID Parsing

When the user enters a Fix Match string, the plugin resolves it as follows:

1. `https://hardcover.app/books/{slug}` → extract slug → query `GetBookBySlug` → get integer ID
2. `book:{integer}` → use integer directly
3. Bare integer string → treat as book ID
4. `author:{integer}` → not applicable for book enrichment (ignored or error)

---

## Search Strategy — `SearchAsync`

Because books and audiobooks are HierarchyLevel 0, there is no parent-gating concern. The cascade is two stages.

### Stage 1a — Title + optional author, with year

For each title in `context.AltTitles` (in order):

1. GraphQL search with `query: "{title}"` (phrase-quoted if title contains special chars)
2. Filter candidates by `release_year == context.Year` at scoring time (the Hardcover API doesn't support year as a filter parameter in search)
3. If any candidate scores ≥ threshold → accept and stop

### Stage 1b — Same titles, without year

For each title in `context.AltTitles`:

1. Same search, but ignore year in scoring (no year bonus or penalty)
2. Accept if score ≥ threshold

If both stages return zero candidates, the result is `NotFound`.

### ISBN Short-Circuit

If the file scanner has extracted an ISBN-10 or ISBN-13 from a file tag or NFO sidecar, the plugin can bypass search entirely:

1. Call `GetByIdAsync` with `isbn:{isbn13}` or `isbn10:{isbn10}` format
2. Hardcover accepts ISBNs as alternate lookup keys
3. ISBN match is treated as a confirmed match — no score threshold required

This short-circuit applies when `context.SubItemMetadata` or `fileScannerMetadata` contains an `isbn_13` or `isbn_10` field.

### Scoring Signals

| Signal | Score |
|--------|-------|
| Exact title match (normalised) | +40 |
| Fuzzy title match (Levenshtein ≤ 20% distance) | +20 |
| Year exact match | +20 |
| Year ±1 | +10 |
| Year mismatch > 1 | −10 |
| Author name match (`context.ParentName` or first author from `context.GrandparentName`) | +15 |
| Series name partial match (from `context.Name` or `AltTitles`) | +10 |
| ISBN match | +30 (auto-accept) |

Default acceptance threshold: **50** (same as other plugins).

---

## `GetByIdAsync` — Supported ID Formats

| Input format | Behaviour |
|---|---|
| `book:{id}` | `books_by_pk(id: {id})` direct GraphQL fetch |
| `isbn:{isbn13}` | Search by ISBN-13 (Hardcover supports ISBN lookup) |
| `isbn10:{isbn10}` | Search by ISBN-10 |
| `https://hardcover.app/books/{slug}` | Parse slug → `GetBookBySlug` → `books_by_pk` |

---

## `metadata_json` Storage

All Hardcover data is stored under the full plugin ID key:

```json
{
  "chronicle.plugin.hardcover": {
    "id": 75984,
    "slug": "the-name-of-the-wind",
    "title": "The Name of the Wind",
    "description": "Told in Kvothe's own voice, this is the tale of the magically gifted young man who grows to be the most notorious wizard his world has ever seen.",
    "release_year": 2007,
    "release_date": "2007-03-27",
    "pages": 662,
    "language": "English",
    "isbn_10": "0756404738",
    "isbn_13": "9780756404734",
    "authors": [
      { "id": 1234, "name": "Patrick Rothfuss", "contribution": "Author" }
    ],
    "narrators": ["Nick Podehl"],
    "publisher": "DAW Books",
    "series": [
      { "id": 456, "name": "The Kingkiller Chronicle", "position": 1.0 }
    ],
    "genres": ["Fantasy", "Fiction", "Epic Fantasy"],
    "moods": ["adventurous", "mysterious", "immersive"],
    "tags": ["magic system", "coming of age", "first person narrator"],
    "rating": 4.54,
    "ratings_count": 125847,
    "users_count": 89432,
    "format": "Audio",
    "audio_seconds": 27820,
    "cover_url": "https://cdn.hardcover.app/system/books/75984/cover.jpg",
    "editions_count": 42
  }
}
```

### Chronicle First-Class Field Mappings

| `metadata_json` field | Chronicle field |
|---|---|
| `title` | `media_items.name` |
| `release_year` | `media_items.year` (or extracted from `release_date`) |
| `cover_url` (from `image.url`) | Poster image |
| `description` | Shown in PluginMetadataBox |
| `authors[0].name` | Shown as primary creator |
| `isbn_13` | Stored in `media_external_ids` |

---

## Rate Limiting

The plugin maintains a `SemaphoreSlim` + timestamp guard (same pattern as MusicBrainz):

- Default: 1 req / 1200ms (~50 req/min)
- All calls to `SearchAsync`, `GetByIdAsync`, `GetImageAsync` go through the limiter
- `RateLimitMs` setting overrides the default at plugin configuration time

---

## `GetImageAsync`

Given a cover URL from Hardcover's CDN, download the image bytes via `HttpClient` and return them. The URL comes from `image.url` in the GraphQL response. No additional authentication is required for image CDN URLs.

---

## Background Tasks (manifest)

```json
"background_tasks": [
  {
    "task_id":         "fetch-missing-metadata",
    "display_name":    "Fetch Missing Metadata",
    "description":     "Looks up metadata from Hardcover for newly imported books and audiobooks that don't have it yet.",
    "default_cron":    "0 4 * * *",
    "default_enabled": true
  },
  {
    "task_id":         "resync-all-metadata",
    "display_name":    "Re-sync All Metadata",
    "description":     "Re-downloads all Hardcover metadata to pick up corrections and updated covers.",
    "default_cron":    "0 3 * * 0",
    "default_enabled": false
  }
]
```

---

## File Structure

```
W:\Scripts\Chronicle.Plugin.Hardcover\
├── Chronicle.Plugin.Hardcover.csproj
├── manifest.json
├── HardcoverMetadataProvider.cs      # IMetadataProvider: SearchAsync, GetByIdAsync, GetImageAsync
├── HardcoverClient.cs                # GraphQL HTTP client, rate limiter, auth header injection
├── HardcoverSearcher.cs              # Stage 1a/1b cascade, AltTitles iteration, ISBN short-circuit
├── HardcoverModels.cs                # C# models for GraphQL response deserialization
├── HardcoverMetadataMapper.cs        # Maps HardcoverBook → MediaMetadata + metadata_json shape
└── tests/
    ├── HardcoverSearcherTests.cs
    ├── HardcoverMetadataMapperTests.cs
    └── HardcoverClientTests.cs
```

### `.csproj` Key Settings

```xml
<TargetFramework>net9.0</TargetFramework>
<AssemblyName>Chronicle.Plugin.Hardcover</AssemblyName>

<!-- Chronicle.Plugins + Chronicle.Core must NOT be copied to plugin output dir -->
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
<ProjectReference Include="..\Chronicle\src\Chronicle.Core\Chronicle.Core.csproj"
                  Private="false" ExcludeAssets="runtime" />

<!-- Only NuGet package needed: HTTP client -->
<PackageReference Include="Microsoft.Extensions.Http" Version="9.0.3" />
<PackageReference Include="System.Text.Json" Version="9.0.3" />
```

---

## GraphQL vs REST Notes

Hardcover uses GraphQL exclusively (no REST API). The plugin sends all requests as HTTP POST to `https://api.hardcover.app/v1/graphql` with a JSON body:

```json
{
  "query": "query SearchBooks($query: String!) { ... }",
  "variables": { "query": "The Name of the Wind" }
}
```

The `HardcoverClient` wraps this pattern. Each operation (search, get book, get by slug) has a corresponding method that takes typed parameters and returns typed C# models via `System.Text.Json` deserialization.

---

## `HealthCheckAsync`

```graphql
query { __typename }
```

A minimal introspection query that confirms the API is reachable and the token is valid. Returns `true` if the HTTP response is 200; returns `false` on 401 (invalid token) or network failure.

---

## Acceptance Criteria

1. A book with a known Hardcover ID (`book:75984`) is fetched via `GetByIdAsync` and all fields (title, author, year, description, genres, cover) are stored in `metadata_json`
2. A book searched by title + author returns the correct result with score ≥ threshold
3. A book with a year-prefixed folder name (e.g., `(2007) The Name of the Wind`) is matched to the correct Hardcover entry via Stage 1b if Stage 1a fails
4. Entering a full Hardcover URL in Fix Match (`https://hardcover.app/books/the-name-of-the-wind`) resolves to the correct book integer ID
5. A book not in Hardcover's catalogue is marked `NotFound` after both stages — not `Exhausted`
6. Cover art is downloaded and stored
7. Narrator information (for audiobooks) is stored in the metadata JSON
8. Rate limiting prevents exceeding ~50 req/min

---

## Out of Scope (v1.0)

- Reading user's Hardcover reading list / shelf status (requires OAuth, separate feature)
- Syncing Chronicle interaction events back to Hardcover
- Series as a separate HierarchyLevel container
- Edition-level enrichment (individual ISBN editions vs canonical works)
- Hardcover community reviews / ratings display
