# GoodReads / Open Library Plugin Design

**Date:** 2026-04-07
**Status:** Draft
**Repo:** `W:\Scripts\Chronicle.Plugin.GoodReads\` (to be created)
**Data source:** Open Library (https://openlibrary.org/)
**GoodReads site:** https://www.goodreads.com/

---

## GoodReads API Status — Important Context

The GoodReads public API was **deprecated in December 2020**. The developer portal no longer accepts new API key registrations, existing keys are on a maintenance-only basis with no SLA, and Goodreads has stated the API will be retired. Any plugin built directly against the GoodReads API would be built on a service that can go away without notice.

**Decision:** The Chronicle GoodReads plugin uses the **Open Library API** (https://openlibrary.org/) as its data source. Open Library is:

- Maintained by the Internet Archive — not going away
- Completely free with no API key required
- Has millions of books, editions, authors, ISBNs, and cover art
- Stores cross-references to GoodReads IDs, ISBNs, and other book identifiers
- Used as the data backend by dozens of open-source book tools

**GoodReads compatibility is preserved** in the following way: a GoodReads book URL or numeric ID can be entered in the Fix Match panel, and the plugin resolves the GoodReads ID to an Open Library work via Open Library's built-in cross-reference index. The resulting metadata is Open Library data, but GoodReads URLs are honoured as lookup keys.

The plugin is named "GoodReads" in the UI (since that is the brand users recognise) but the manifest description makes the actual data source clear.

---

## Plugin Identity

```
plugin_id:   chronicle.plugin.goodreads
name:        GoodReads
version:     1.0.0
author:      Chronicle Contributors
entry_type:  Chronicle.Plugin.GoodReads.GoodReadsMetadataProvider
fixMatchHint: Enter a GoodReads book URL (e.g. https://www.goodreads.com/book/show/186074) or an Open Library ID (e.g. work:OL45804W)
```

---

## Supported Media Types

Same as Hardcover — books and audiobooks are modelled as flat HierarchyLevel 0 items. Series membership is stored as metadata.

| Media Type | HierarchyLevel | Notes |
|------------|----------------|-------|
| `books` | 0 | Any book |
| `audiobooks` | 0 | Audiobooks |

---

## Settings Schema

No API key or authentication is required for Open Library. The optional Google Books key enables fallback enrichment (richer descriptions, ratings) when Open Library lacks them.

| Key | Type | Required | Default | Notes |
|-----|------|----------|---------|-------|
| `GoogleBooksApiKey` | Password | No | — | Optional. A free Google Books API key (from Google Cloud Console). When provided, the plugin falls back to Google Books for descriptions and ratings when Open Library has none. 1,000 req/day free quota. |
| `MaxRetries` | Number | No | `3` | Per-item failure limit before `Exhausted` |
| `RateLimitMs` | Number | No | `1000` | ms between API calls; Open Library asks for ≤1 req/sec per their ToS |
| `UserAgent` | Text | No | `Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)` | Required by Open Library; identify your app in the User-Agent |

> **Note:** Because no API key is required for the primary source, users can install and use this plugin with **zero configuration** — just install and enable. This makes it an excellent default book metadata source. The optional Google Books key is a quality-of-life enhancement for users who want fuller descriptions.

### Data Source — Open Library

Open Library (https://openlibrary.org/) is maintained by the Internet Archive. Its data is licensed **CC0 (public domain)** — fully open for any use, including commercial software, without attribution requirements. It is not going anywhere.

Open Library explicitly encourages programmatic access. Rate limits are not enforced mechanically but the project asks users to be polite (≤1 req/sec). Bulk crawling of the entire catalogue is inappropriate; on-demand enrichment for a personal media server is exactly the expected use case.

### Why Not GoodReads Directly?

The GoodReads public API was shut down on **December 8, 2020**. Amazon closed new key registrations before that and then retired all API access. All API endpoints now return HTTP 410 Gone. GoodReads' Terms of Service explicitly prohibit scraping, and the site uses Cloudflare bot detection that makes HTML scraping unreliable. A distributed plugin built on GoodReads scraping would violate ToS and break without notice. Open Library is the correct alternative.

### Why Not Google Books Alone?

Google Books API provides good descriptions and ratings but has significant gaps: **no series data**, poor cover art (thumbnail only, ~128px), a hard 1,000 req/day free quota, and an API key requirement. It works well as a *fallback enrichment source* but not as the sole foundation.

---

## API Details

| Property | Value |
|----------|-------|
| Base URL | `https://openlibrary.org` |
| Protocol | REST/JSON over HTTPS |
| Authentication | None (anonymous) |
| Rate limit | 1 request/second (per Open Library ToS) |
| Response format | JSON |

### Endpoint: Search Books

```
GET https://openlibrary.org/search.json
    ?title={title}
    &author={author}         (optional)
    &fields=key,title,author_name,author_key,first_publish_year,isbn,
            cover_i,subject,number_of_pages_median,id_goodreads,
            ia,has_fulltext,edition_count
    &limit=10
```

Response (simplified):
```json
{
  "numFound": 142,
  "docs": [
    {
      "key": "/works/OL45804W",
      "title": "The Name of the Wind",
      "author_name": ["Patrick Rothfuss"],
      "author_key": ["/authors/OL1394865A"],
      "first_publish_year": 2007,
      "isbn": ["9780756404734", "9780756404079"],
      "cover_i": 8739161,
      "subject": ["Fantasy fiction", "Magic"],
      "id_goodreads": ["186074"],
      "edition_count": 87
    }
  ]
}
```

### Endpoint: Get Work by OLID

```
GET https://openlibrary.org/works/{olid}.json
```

Response includes: `title`, `description`, `subjects`, `subject_places`, `subject_times`, `created`, `first_publish_date`, `covers` (array of cover IDs), `links`, `authors` (OLID refs).

### Endpoint: Get Author by OLID

```
GET https://openlibrary.org/authors/{olid}.json
```

Returns: `name`, `bio`, `birth_date`, `death_date`, `photos`, `links`.

### Endpoint: ISBN Lookup (direct edition)

```
GET https://openlibrary.org/api/books
    ?bibkeys=ISBN:{isbn13}
    &format=json
    &jscmd=data
```

Returns the edition record keyed by `"ISBN:{isbn13}"`, with full publisher, publish date, number of pages, cover, subjects, and a link to the containing work.

### Endpoint: GoodReads ID Cross-Reference Lookup (for Fix Match)

```
GET https://openlibrary.org/search.json
    ?q=identifiers.goodreads%3A{goodreadsId}
    &fields=key,title,author_name,first_publish_year,cover_i
    &limit=1
```

Resolves a GoodReads book ID to an Open Library work key. This powers the Fix Match workflow when users enter a GoodReads URL.

### Cover Art

```
https://covers.openlibrary.org/b/id/{cover_id}-L.jpg
https://covers.openlibrary.org/b/isbn/{isbn13}-L.jpg
https://covers.openlibrary.org/b/olid/{olid}-L.jpg
```

Three sizes available by replacing `-L` with `-M` (medium) or `-S` (small). The plugin always fetches `-L` (large).

---

## External ID Format

| Entity | Format | Example |
|--------|--------|---------|
| Work (canonical) | `work:{olid}` | `work:OL45804W` |
| Author | `author:{olid}` | `author:OL1394865A` |

The Open Library Work OLID (`OL{n}W`) is the canonical external ID. Edition OLIDs (`OL{n}M`) and ISBN-based editions are used internally during lookup but the stored `external_id` is always the Work ID for consistency.

GoodReads IDs are stored in `metadata_json` under `id_goodreads` for reference but are not used as the primary external ID.

---

## Fix Match — ID Resolution

When the user enters a Fix Match string, the plugin resolves it as follows:

| Input format | Resolution |
|---|---|
| `https://www.goodreads.com/book/show/{id}` | Extract numeric GoodReads ID → `GET /search.json?q=identifiers.goodreads:{id}` → get OLID |
| `https://www.goodreads.com/book/show/{id}.{slug}` | Same — only the numeric ID before the `.` is used |
| `work:{olid}` | Use OLID directly → `GET /works/{olid}.json` |
| `https://openlibrary.org/works/{olid}` | Parse OLID from URL |
| `https://openlibrary.org/works/{olid}/The_Name_of_the_Wind` | Parse OLID from URL |
| ISBN-10 or ISBN-13 (13-digit number) | `GET /api/books?bibkeys=ISBN:{isbn}` → resolve to work key |
| Bare OLID (`OL45804W`) | Treat as `work:{olid}` |

---

## Search Strategy — `SearchAsync`

### Stage 1a — Title + optional author, with year

For each title in `context.AltTitles` (in order):

1. `GET /search.json?title={title}&author={parentName}&fields=...&limit=10`
   (`author` omitted if `context.ParentName` is null)
2. Score each result (see Scoring Signals below)
3. Year filter applied at scoring time (Open Library doesn't support year as a query param reliably)
4. Accept if top candidate scores ≥ threshold

### Stage 1b — Same titles, without year

For each title in `context.AltTitles`:

1. Same search, year signals omitted from scoring
2. Accept if score ≥ threshold

If both stages return zero candidates → result is `NotFound`.

### ISBN Short-Circuit

If `fileScannerMetadata` or file tags contain an ISBN-13 or ISBN-10:

1. `GET /api/books?bibkeys=ISBN:{isbn}&format=json&jscmd=data`
2. Extract the work key from the edition record
3. Fetch the full work via `GET /works/{olid}.json`
4. ISBN match is treated as a confirmed result — no threshold check

This is the most reliable lookup path for properly tagged audiobook files.

### Scoring Signals

| Signal | Score |
|--------|-------|
| Exact title match (normalised, lower) | +40 |
| Fuzzy title match (Levenshtein ≤ 20%) | +20 |
| First publish year exact match | +20 |
| First publish year ±1 | +10 |
| Year mismatch > 1 | −10 |
| Author name match (`context.ParentName`) | +15 |
| GoodReads ID cross-reference match | +30 (auto-accept) |
| ISBN match | +30 (auto-accept) |

Default acceptance threshold: **50**.

---

## `GetByIdAsync` — Supported ID Formats

| Input format | Behaviour |
|---|---|
| `work:{olid}` | `GET /works/{olid}.json` |
| `author:{olid}` | `GET /authors/{olid}.json` (for author-level enrichment if added in future) |
| `isbn:{isbn13}` | `GET /api/books?bibkeys=ISBN:{isbn13}` → resolve to work |
| `isbn10:{isbn10}` | Same, ISBN-10 variant |
| `goodreads:{id}` | GoodReads ID cross-reference search → resolve to work OLID |

---

## `metadata_json` Storage

All data stored under the full plugin ID key:

```json
{
  "chronicle.plugin.goodreads": {
    "olid": "OL45804W",
    "title": "The Name of the Wind",
    "description": "Told in Kvothe's own voice, this is the tale of the magically gifted young man who grows to be the most notorious wizard his world has ever seen.",
    "first_publish_year": 2007,
    "subjects": ["Fantasy fiction", "Magic", "Epic fantasy"],
    "subject_places": ["Fictional kingdoms"],
    "subject_times": [],
    "authors": [
      {
        "olid": "OL1394865A",
        "name": "Patrick Rothfuss",
        "birth_date": "1973",
        "bio": "Patrick James Rothfuss is an American author of epic fantasy..."
      }
    ],
    "covers": [8739161, 8739162],
    "cover_url": "https://covers.openlibrary.org/b/id/8739161-L.jpg",
    "edition_count": 87,
    "id_goodreads": ["186074"],
    "id_librarything": ["3869"],
    "isbn": ["9780756404734", "9780756404079"],
    "links": [
      { "title": "Author's Website", "url": "https://patrickrothfuss.com" }
    ],
    "data_source": "openlibrary",
    "google_books_id": "zyTCAlFPjgYC",
    "google_books_description": "Told in Kvothe's own voice...",
    "google_books_rating": 4.5,
    "google_books_ratings_count": 12847
  }
}
```

When `GoogleBooksApiKey` is configured, the plugin enriches the result with Google Books data after the Open Library lookup. Fields prefixed `google_books_*` are sourced from Google Books. The `description` field at the top level is populated from Open Library if present; if Open Library has no description, the `google_books_description` value is promoted to fill it.

### Chronicle First-Class Field Mappings

| `metadata_json` field | Chronicle field |
|---|---|
| `title` | `media_items.name` |
| `first_publish_year` | `media_items.year` |
| `cover_url` | Poster image |
| `description` | Shown in PluginMetadataBox |
| `authors[0].name` | Shown as primary creator |
| `isbn[0]` | `media_external_ids` |
| `id_goodreads[0]` | `media_external_ids` (as `goodreads:{id}`) |

---

## Google Books Fallback Enrichment (optional)

When `GoogleBooksApiKey` is set, after a successful Open Library match the plugin makes a single supplemental call to Google Books to fill in missing fields:

```
GET https://www.googleapis.com/books/v1/volumes
    ?q=isbn:{isbn13}
    &key={GoogleBooksApiKey}
```

Or if no ISBN is available:
```
GET https://www.googleapis.com/books/v1/volumes
    ?q=intitle:{title}+inauthor:{author}
    &key={GoogleBooksApiKey}
    &maxResults=3
```

Fields used from Google Books response (`volumeInfo`):
- `description` — only written to metadata if Open Library has no description
- `averageRating` / `ratingsCount` — stored as `google_books_rating` / `google_books_ratings_count`
- `categories` — merged into `subjects` array (deduplicated)
- `volumeId` — stored as `google_books_id`

**Cover art is NOT used from Google Books** — Google only provides thumbnail-size images (~128px). Open Library's covers CDN provides full-size images.

This is a best-effort supplemental call. If it fails (rate limit, missing key, no match), the item is still marked `Completed` with the Open Library data — the Google Books enrichment is not retried separately.

---

## Rate Limiting

Same `SemaphoreSlim` + timestamp pattern as MusicBrainz. Default: 1 req/second. Unlike MusicBrainz (which has authenticated vs anonymous tiers), Open Library has a single unauthenticated rate with their requested limit of ≤1 req/sec stated in their documentation. Google Books calls share the same rate limiter since they are supplemental to Open Library calls (one GB call per OL call at most).

---

## `HealthCheckAsync`

```
GET https://openlibrary.org/works/OL45804W.json
```

A known-good book fetch (The Name of the Wind, Patrick Rothfuss). Returns `true` if HTTP 200, `false` on any error. No auth to check.

---

## Background Tasks (manifest)

```json
"background_tasks": [
  {
    "task_id":         "fetch-missing-metadata",
    "display_name":    "Fetch Missing Metadata",
    "description":     "Looks up book metadata from Open Library for newly imported books and audiobooks.",
    "default_cron":    "0 4 * * *",
    "default_enabled": true
  },
  {
    "task_id":         "resync-all-metadata",
    "display_name":    "Re-sync All Metadata",
    "description":     "Re-downloads all Open Library metadata to pick up corrections, new editions, and updated covers.",
    "default_cron":    "0 3 * * 0",
    "default_enabled": false
  }
]
```

---

## File Structure

```
W:\Scripts\Chronicle.Plugin.GoodReads\
├── Chronicle.Plugin.GoodReads.csproj
├── manifest.json
├── GoodReadsMetadataProvider.cs      # IMetadataProvider: SearchAsync, GetByIdAsync, GetImageAsync
├── OpenLibraryClient.cs              # HTTP client, rate limiter, all API calls
├── GoodReadsSearcher.cs              # Stage 1a/1b cascade, AltTitles iteration, ISBN short-circuit
├── GoodReadsModels.cs                # C# models for Open Library JSON deserialization
├── GoodReadsMetadataMapper.cs        # Maps OpenLibraryWork → MediaMetadata + metadata_json
└── tests/
    ├── GoodReadsSearcherTests.cs
    ├── GoodReadsMetadataMapperTests.cs
    └── OpenLibraryClientTests.cs
```

> **Naming note:** The project and class names use "GoodReads" (the brand the user sees) while the internal implementation uses "OpenLibrary" for the HTTP client and models — this makes it clear in code where the data actually comes from.

### `.csproj` Key Settings

```xml
<TargetFramework>net9.0</TargetFramework>
<AssemblyName>Chronicle.Plugin.GoodReads</AssemblyName>

<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
<ProjectReference Include="..\Chronicle\src\Chronicle.Core\Chronicle.Core.csproj"
                  Private="false" ExcludeAssets="runtime" />

<PackageReference Include="Microsoft.Extensions.Http" Version="9.0.3" />
<PackageReference Include="System.Text.Json" Version="9.0.3" />
```

---

## Comparison with Hardcover Plugin

| Feature | Hardcover | GoodReads (Open Library) |
|---------|-----------|--------------------------|
| API type | GraphQL | REST/JSON |
| Auth required | Yes — Bearer token | No — anonymous |
| Rate limit | ~50 req/min | ~60 req/min (1/sec) |
| Book coverage | ~4M titles | ~40M+ works |
| Audiobook-specific data | Narrators, audio length | Limited (edition-level) |
| Cover art | Hardcover CDN | Open Library covers CDN |
| Series data | Native (Hardcover tracks series) | Limited (from subjects only) |
| GoodReads Fix Match | No (different platform) | Yes — via cross-reference |
| ISBN lookup | Yes | Yes |
| Community ratings | Yes (Hardcover users) | No (ratings removed from OL) |

For users who track audiobooks and want narrator info, **Hardcover** is the richer source. For users who want the largest possible book coverage with zero configuration, **GoodReads (Open Library)** is the better default.

Both plugins can be active simultaneously — Chronicle will show both metadata boxes on a book's detail page.

---

## Future Considerations (Out of Scope for v1.0)

- **Direct GoodReads integration:** If GoodReads ever re-opens API access (or if an OAuth flow becomes available), the `GoodReadsMetadataProvider` could be extended to also fetch ratings, reviews, and shelf data using the same external ID infrastructure — since we already store GoodReads IDs
- **Google Books fallback:** Google Books API (requires an API key) could be added as a Stage 2 fallback if Open Library returns no results — particularly useful for newer releases not yet in OL
- **Series containers:** A future `book_series` media type at HierarchyLevel 0 could group books by series, with individual books as HierarchyLevel 1 children
- **Reading progress sync:** Chronicle interaction events mapped to GoodReads shelf moves (requires authenticated GoodReads access)
- **Narrator enrichment:** Cross-reference audiobook narrators with MusicBrainz person records (narrators often have MusicBrainz artist pages)

---

## Acceptance Criteria

1. A book searched by title + author returns the correct Open Library work with score ≥ threshold
2. Entering `https://www.goodreads.com/book/show/186074` in Fix Match resolves to the correct Open Library work (`OL45804W`)
3. Entering `work:OL45804W` in Fix Match resolves directly
4. An ISBN-13 in file scanner metadata triggers the ISBN short-circuit and bypasses search
5. A book not in Open Library is marked `NotFound` after both stages
6. Cover art is downloaded and stored from the Open Library covers CDN
7. The `id_goodreads` cross-reference value is stored in `metadata_json` when present
8. Rate limiting stays at or below 1 req/sec
9. `HealthCheckAsync` returns `true` when the Open Library API is reachable
