# Chronicle.Plugin.Hardcover

Metadata source plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) that
fetches book and audiobook metadata from [Hardcover](https://hardcover.app/).

**Plugin ID:** `chronicle.plugin.hardcover`
**Version:** 1.0.0
**Media Types:** Books (`books`), Audiobooks (`audiobooks`)
**Auth:** Personal API token (hardcover.app/account/api)
**API:** Hardcover GraphQL API — `https://api.hardcover.app/v1/graphql`

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

[Hardcover](https://hardcover.app/) is a social book-tracking platform with a public
GraphQL API. It provides rich book and audiobook metadata: title, authors, narrators,
publisher, publication date, description, genres, moods, community tags, series position,
cover art, and ISBNs.

This plugin requires a personal access token from the user's Hardcover account settings
page. A GraphQL client is used for all requests; there is no REST API.

---

## Supported Media Types

Books and audiobooks are flat **HierarchyLevel 0** items. Series membership is stored as
metadata (series name + numeric position) — not as a parent-child hierarchy. This matches
how Hardcover models data and avoids artificial series container nodes.

| Media Type | HierarchyLevel | Priority | Notes |
|------------|----------------|----------|-------|
| `books` | 0 | 1 | All books — fiction, non-fiction, graphic novels, etc. |
| `audiobooks` | 0 | 1 | Audiobooks (narrators + audio duration stored in metadata) |

> **Future:** A `book_series` media type could be added as a HierarchyLevel 0 container
> with individual books as Level 1 children. Out of scope for v1.0.

---

## Settings Schema

| Key | Label | Type | Required | Default | Notes |
|-----|-------|------|----------|---------|-------|
| `ApiToken` | Hardcover API Token | Password | **Yes** | — | From hardcover.app/account/api. Tokens expire January 1 each year. Stored encrypted. |
| `MaxRetries` | Max Retries | Number | No | `3` | Per-item failure limit before `Exhausted` |
| `RateLimitMs` | Rate Limit (ms) | Number | No | `1000` | ms between requests; Hardcover allows 60 req/min |

---

## API Details

| Property | Value |
|----------|-------|
| Endpoint | `https://api.hardcover.app/v1/graphql` |
| Protocol | GraphQL over HTTPS (POST) |
| Authentication | `Authorization: Bearer {ApiToken}` |
| Rate limit | 60 requests/minute; 30-second query timeout; max query depth 3 |
| Response format | `application/json` |

All requests are HTTP POST with body:
```json
{ "query": "...", "variables": { ... } }
```

### GraphQL — Search Books

Hardcover's `search()` function uses Meilisearch internally. **The `results` field returns a raw `jsonb` blob**, not typed GraphQL fields — the response must be deserialized as a `JsonElement` and mapped to C# models manually.

```graphql
query SearchBooks($query: String!, $limit: Int) {
  search(query: $query, query_type: "Book", per_page: $limit, page: 1) {
    results
  }
}
```

The `results` jsonb contains an object like:
```json
{
  "found": 142,
  "hits": [
    {
      "document": {
        "id": 75984,
        "slug": "the-name-of-the-wind",
        "title": "The Name of the Wind",
        "release_year": 2007,
        "image": { "url": "https://cdn.hardcover.app/..." },
        "author_names": ["Patrick Rothfuss"],
        "series_names": ["The Kingkiller Chronicle"]
      }
    }
  ]
}
```

**Alternative: structured filter search** (useful for ISBN lookups or exact-match):

```graphql
query SearchBooksByTitle($title: String!, $author: String) {
  books(
    where: {
      _and: [
        { title: { _ilike: $title } }
        { contributions: { author: { name: { _ilike: $author } } } }
      ]
    }
    limit: 10
    order_by: { users_count: desc }
  ) {
    id
    slug
    title
    release_year
    rating
    image { url }
    contributions { author { name } }
    book_series { position series { id name } }
  }
}
```

Use `search()` for `SearchAsync` (faster, full-text). Use `books()` filter for ISBN/exact-match lookups.

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

### GraphQL — Get Book by Slug (Fix Match URL path)

```graphql
query GetBookBySlug($slug: String!) {
  books(where: { slug: { _eq: $slug } }, limit: 1) {
    id
    title
    slug
  }
}
```

### HealthCheckAsync

```graphql
query { __typename }
```

A minimal introspection query. Returns `true` on HTTP 200; `false` on 401 (bad token) or
network failure.

---

## External ID Format

| Entity | Format | Example |
|--------|--------|---------|
| Book | `book:{hardcoverId}` | `book:75984` |
| Author | `author:{hardcoverId}` | `author:12345` |

The `hardcoverId` is the integer `id` field from GraphQL. Slugs are used only for Fix
Match URL parsing; the canonical `external_id` stored in the DB always uses the integer form.

---

## Fix Match Resolution

`fixMatchHint`: *Enter a Hardcover book URL (e.g. https://hardcover.app/books/the-name-of-the-wind) or a book ID (e.g. book:75984)*

| Input | Resolution |
|-------|-----------|
| `https://hardcover.app/books/{slug}` | Extract slug → `GetBookBySlug` → integer ID |
| `book:{integer}` | Use integer directly |
| Bare integer string | Treat as book ID |
| `author:{integer}` | Not applicable for book enrichment (ignored) |

---

## Search Strategy

`SearchAsync` runs a two-stage cascade. Because books/audiobooks are HierarchyLevel 0,
there is no parent-gating and no parent name constraint.

### Stage 1a — AltTitles with year

For each title in `context.AltTitles` (in order: PreciseName → year-stripped → filenameStem → qualifier-stripped):

1. GraphQL search `query: "{title}"` with `limit: 10`
2. Score each result (see table below)
3. Year comparison applied at scoring time (no native year filter in Hardcover search API)
4. Accept if top candidate scores ≥ 50 — stop

### Stage 1b — AltTitles without year

Same iteration, year signals excluded from scoring. Accept if score ≥ 50.

If both stages yield zero candidates → `NotFound`.

### ISBN Short-Circuit

If `fileScannerMetadata` or file tags contain an ISBN-13 or ISBN-10, bypass search:

1. Call `GetByIdAsync("isbn:{isbn13}")` or `GetByIdAsync("isbn10:{isbn10}")`
2. ISBN match is a confirmed result — threshold not applied

### Scoring Signals

| Signal | Score |
|--------|-------|
| Exact title match (normalised, lower) | +40 |
| Fuzzy title match (Levenshtein ≤ 20% distance) | +20 |
| Year exact match | +20 |
| Year ±1 | +10 |
| Year mismatch > 1 | −10 |
| Author name match (`context.ParentName`) | +15 |
| Series name partial match | +10 |
| ISBN match | auto-accept |

Default acceptance threshold: **50**

---

## GetByIdAsync

| Input format | Behaviour |
|---|---|
| `book:{id}` | `books_by_pk(id: {id})` — direct GraphQL fetch |
| `isbn:{isbn13}` | Hardcover ISBN-13 lookup |
| `isbn10:{isbn10}` | Hardcover ISBN-10 lookup |
| `https://hardcover.app/books/{slug}` | Parse slug → `GetBookBySlug` → `books_by_pk` |

---

## `metadata_json` Storage

All Hardcover data stored under the full plugin ID key in the item's `metadata_json`:

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
    "asin": "B002SXUAVY",
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
    "cached_tags": { "Genre": ["Fantasy", "Fiction"], "Mood": ["adventurous"], "Trope": ["magic system"] },
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

### First-Class Chronicle Field Mappings

| `metadata_json` field | Chronicle field |
|---|---|
| `title` | `media_items.name` |
| `release_year` | `media_items.year` |
| `cover_url` | Poster image |
| `description` | PluginMetadataBox overview |
| `authors[0].name` | Primary creator display |
| `isbn_13` | `media_external_ids` (source `hardcover_isbn`) |

---

## Rate Limiting

`SemaphoreSlim(1,1)` + `Stopwatch` elapsed guard — same pattern as MusicBrainz.

- Default gap: 1000ms (60 req/min hard limit; 1 req/sec gives comfortable headroom)
- Applies to ALL outbound calls (search, getById, getImage)
- `RateLimitMs` setting overrides at configure time; floor is 200ms (hard-coded)

---

## Background Tasks

| Task | Schedule | Enabled by default |
|------|----------|-------------------|
| `fetch-missing-metadata` | Daily at 04:00 | Yes |
| `resync-all-metadata` | Weekly Sunday 03:00 | No |

---

## Repository Structure

```
W:\Scripts\Chronicle.Plugin.Hardcover\
├── Chronicle.Plugin.Hardcover.csproj
├── manifest.json
├── HardcoverMetadataProvider.cs      # IMetadataProvider implementation
├── HardcoverClient.cs                # GraphQL HTTP client + rate limiter + auth
├── HardcoverSearcher.cs              # SearchAsync cascade, AltTitles iteration, ISBN short-circuit
├── HardcoverModels.cs                # C# models for GraphQL response deserialization
├── HardcoverMetadataMapper.cs        # HardcoverBook → MediaMetadata + metadata_json
└── tests/
    ├── HardcoverSearcherTests.cs
    ├── HardcoverMetadataMapperTests.cs
    └── HardcoverClientTests.cs
```

---

## Building & Packaging

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>Chronicle.Plugin.Hardcover</AssemblyName>
    <RootNamespace>Chronicle.Plugin.Hardcover</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <!-- Local path reference during development.
         Replace with a NuGet package reference once Chronicle.Plugins is published. -->
    <ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                      Private="false"
                      ExcludeAssets="runtime" />
    <!-- Chronicle.Core is a transitive dep of Chronicle.Plugins. Private="false" prevents
         it being copied into the plugin output dir (which would cause PluginLoadContext
         to load the wrong copy at runtime). -->
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

Deploy output to: `plugins/hardcover/` under the Chronicle API publish directory.

---

## Implementation Notes

- **`search()` returns `jsonb`** — the `results` field is a raw JSON blob (Meilisearch response). Deserialise as `JsonElement`, navigate `results.hits[].document`. Do not try to add typed GraphQL sub-fields to `search { results { ... } }` — it won't work.
- **ISBNs are on editions, not on books** — fetch via `default_physical_edition { isbn_13 isbn_10 }` or query `editions(where: { isbn_13: { _eq: "..." } }) { book { id slug } }` for ISBN lookups.
- **Genres come from `cached_tags["Genre"]`** — a jsonb dictionary where keys are tag categories (Genre, Mood, Trope, etc.) and values are string arrays ordered by frequency. Parse as `Dictionary<string, string[]>`.
- **Series position can be decimal** — `position: 1.5` is valid (novellas between books). Store as `double`, display as `1.5` not `2`.
- **Token expiry** — Hardcover tokens reset every January 1. Surface a descriptive error when 401 is received so the user knows to regenerate the token.
- **`audio_seconds`** exists on both the book and on individual editions — prefer the edition value for precision.
- **Max query depth 3** — don't nest deeper than `book → edition → publisher`. Avoid `book → contributions → author → contributions → book` chains.

---

## Acceptance Criteria

1. `book:75984` resolves via `GetByIdAsync` — all fields stored in `metadata_json`
2. Title + author search returns correct result at score ≥ 50
3. Year-prefixed folder name (`(2007) The Name of the Wind`) matched via Stage 1b
4. Fix Match with full URL `https://hardcover.app/books/the-name-of-the-wind` resolves correctly
5. Book absent from Hardcover → `NotFound` (not `Exhausted`)
6. Cover art downloaded and stored
7. Narrator names stored for audiobook items
8. Rate limiting stays ≤ 50 req/min

---

## Out of Scope (v1.0)

- Reading user's Hardcover shelves / reading status (requires OAuth)
- Syncing Chronicle watch events back to Hardcover
- Series as a HierarchyLevel container
- Edition-level enrichment
- Community reviews display

---

*Chronicle.Plugin.Hardcover is an independent community plugin and is not affiliated with
or endorsed by Hardcover.*
