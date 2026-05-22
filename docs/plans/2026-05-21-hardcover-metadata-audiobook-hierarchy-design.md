# Hardcover Metadata Provider + Audiobook Hierarchy Rework

**Date:** 2026-05-21  
**Status:** Approved  
**Supersedes:** `2026-04-07-hardcover-plugin-design.md`

---

## Overview

This design covers two tightly coupled changes:

1. **Audiobook hierarchy rework** — both `books` and `audiobooks` media types move from flat
   (HierarchyLevel 0) to a three-level tree: **Author → Series (optional) → Book**.
   MusicBrainz and the file scanner are updated to match. The `"book"` media type name is
   renamed to `"books"` everywhere (import provider, services, UI).

2. **Hardcover metadata provider** — a new `HardcoverMetadataProvider : IMetadataProvider`
   is added to the existing `Chronicle.Plugin.Hardcover` DLL alongside the already-deployed
   `HardcoverImportProvider`. It supports both `books` and `audiobooks` at all three
   hierarchy levels.

---

## Hierarchy: Author → Series? → Book

```
HierarchyLevels = 3
HierarchyLabels = ["Author", "Series", "Book"]
```

| Situation | Chronicle tree |
|-----------|---------------|
| Book in a series | Author (L0) → Series (L1) → Book (L2) |
| Standalone book | Author (L0) → Book (L1) — no Series node |
| Unknown author | "Unknown" stub (L0) → Book (L1 or L2) |
| No series identified | Book at L1 directly under Author; series can be assigned later via Fix Match |

The max depth is 3. Not all branches reach it. The library root shows Authors and
the user drills in to see Series and Books. Standalone books appear as direct children
of the Author node alongside any Series nodes.

This hierarchy applies to both `books` and `audiobooks`. It replaces the previous
flat (HierarchyLevels=1) audiobooks declaration in MusicBrainz.

---

## "book" → "books" Rename

The Hardcover import provider currently emits `MediaType: "book"` (singular). Chronicle's
media type is `"books"` (plural, consistent with `"movies"`, `"music"`, etc.). Every
occurrence of the string `"book"` used as a media type name must be updated:

- `HardcoverImportProvider.cs` — `GetWatchHistoryAsync`, `GetRatingsAsync`, `GetWatchlistAsync`
- `ImportedWatchEvent` / `ImportedRating` / `ImportedWatchlistEntry` construction
- Any hardcoded `"book"` strings in `FileScanService`, `MetadataEnrichmentService`,
  `SyncOrchestrationService`, and any other services that filter by media type name
- UI: Add Media page, scan folder media type selector, any dropdown or label that lists
  media types by name
- DB seed / migration: if `"book"` is stored as a `media_types.name` row it must match
  what the plugins now declare

---

## File Scanner Changes

### 1. `CollapseAudiobooksToFolders` — sum file durations

The representative `ScannedFile` gets a new `TotalDurationSeconds` field populated by
summing the `AudioDuration` (seconds) from every file in the collapsed group. This is the
source of truth for audiobook `RuntimeMinutes`.

TagLibSharp already reads duration for each file during scanning. The sum replaces the
current single-file duration, which was only one chapter.

### 2. New: `GroupAudiobooksByAuthorAndSeries`

After `CollapseAudiobooksToFolders` produces a flat list of one `ScannedFile` per book,
a second pass organises them into the three-level tree before import:

```
Input:  List<ScannedFile>  (one entry per book folder)
Output: Tree of ScanGroup  (Author → Series? → Book)
```

Grouping rules:
- **Author key**: `AudioAlbumArtist` tag → `AudioArtist` tag → parent folder name →
  `"Unknown"` if all absent
- **Series key**: `AudioGrouping` tag → series parsed from folder name by
  `ParseAudiobookFolderName` → absent (book goes directly under Author)
- Books with the same Author+Series key share a Series node
- Books with the same Author but no Series key attach directly to the Author node

This method is specific to audiobook scanning and runs in addition to (not instead of)
`ScanGroupingService`, which handles the general hierarchical case for other media types.

---

## MusicBrainz Plugin Changes

### `GetSupportedMediaTypes()` — audiobooks

```csharp
new MediaTypeSupport
{
    MediaTypeName   = "audiobooks",
    DisplayName     = "Audiobooks",
    HierarchyLevels = 3,
    HierarchyLabels = ["Author", "Series", "Book"],
    InteractionVerb = "listened",
    DefaultPriority = 10,
    SupportedFields = ["title", "overview", "year", "poster_url", "genres",
                       "cast", "rating", "tags"],
}
```

### `SearchAsync` routing

| HierarchyLevel | Search |
|---------------|--------|
| 0 — Author | `artist?query={name}` (already implemented) |
| 1 — Series | `series?query={name}` — **nice to have; empty result is not an error** |
| 2 — Book | `release-group?query={name}&secondarytype:Audiobook` (already implemented) |

If the series search returns no results, the book remains at Level 1 under its Author.
No enrichment failure is raised. Hardcover will cover series identification.

### New external ID

`series:{mbid}` — used only when MusicBrainz finds a named series entity.
`artist:{mbid}` and `release-group:{mbid}` are unchanged.

---

## Hardcover Metadata Provider

### Plugin identity

The new class lives in the existing `Chronicle.Plugin.Hardcover` project. `PluginRegistry`
auto-discovers both `IImportProvider` and `IMetadataProvider` implementations in the same DLL.

```
PluginId  = "hardcover"          (same as import provider — shared settings)
Name      = "Hardcover"
Version   = "1.1.0"
Author    = "Michael Beck"
```

Settings key `"api_token"` is shared with `HardcoverImportProvider` — one token,
both interfaces.

### Supported media types

```csharp
new MediaTypeSupport
{
    MediaTypeName   = "books",
    DisplayName     = "Books",
    HierarchyLevels = 3,
    HierarchyLabels = ["Author", "Series", "Book"],
    DefaultPriority = 10,
    SupportedFields = ["title", "overview", "year", "poster_url", "genres",
                       "cast", "rating", "tags"],
},
new MediaTypeSupport
{
    MediaTypeName   = "audiobooks",
    DisplayName     = "Audiobooks",
    HierarchyLevels = 3,
    HierarchyLabels = ["Author", "Series", "Book"],
    InteractionVerb = "listened",
    DefaultPriority = 10,
    SupportedFields = ["title", "overview", "year", "poster_url", "genres",
                       "cast", "rating", "runtime_minutes", "tags"],
}
```

---

## External ID Formats

| Level | Entity | Format | Example |
|-------|--------|--------|---------|
| 0 | Author | `hardcover:author:{id}` | `hardcover:author:4821` |
| 1 | Series | `hardcover:series:{id}` | `hardcover:series:678` |
| 2 | Book | `hardcover:{id}` | `hardcover:12345` |

The bare `hardcover:{id}` format (no entity prefix) is used for books to stay
compatible with what the import provider already writes to `media_external_ids`.

---

## GraphQL Queries

### Search — Author (Level 0)

```graphql
query SearchAuthors($q: String!, $n: Int!) {
  search(query: $q, query_type: "Author", per_page: $n) {
    results
  }
}
```

`results` is jsonb. Expected fields in each hit: `id`, `name`, `slug`, `image_url`,
`books_count`.

### Search — Series (Level 1)

```graphql
query SearchSeries($q: String!, $n: Int!) {
  search(query: $q, query_type: "Series", per_page: $n) {
    results
  }
}
```

Expected fields: `id`, `name`, `slug`, `author_name`, `primary_books_count`.

### Search — Book (Level 2)

```graphql
query SearchBooks($q: String!, $n: Int!) {
  search(query: $q, query_type: "book", per_page: $n) {
    results
  }
}
```

Expected fields: `id`, `title`, `release_year`, `image`, `author_names`,
`cached_image`.

### Get Book by ID

```graphql
query GetBook($id: Int!) {
  books(where: { id: { _eq: $id } }) {
    id
    title
    subtitle
    description
    release_year
    pages
    rating
    ratings_count
    cached_tags
    image { url }
    contributions {
      author { id name }
      contribution
    }
    book_series {
      position
      series { id name }
    }
    book_mappings { isbn_13 isbn_10 }
    default_physical_edition {
      audio_seconds
      narrations { narrator { name } }
    }
  }
}
```

### Get Series by ID

```graphql
query GetSeries($id: Int!) {
  series(where: { id: { _eq: $id } }) {
    id
    name
    description
    is_completed
    book_series(order_by: { position: asc }, limit: 1) {
      book { image { url } }
    }
  }
}
```

The first book's cover is used as the series poster (Hardcover stores no separate
series image in the public schema).

### Get Author by ID

```graphql
query GetAuthor($id: Int!) {
  authors(where: { id: { _eq: $id } }) {
    id
    name
    bio
    image { url }
  }
}
```

### Get by Slug (Fix Match URL resolution)

```graphql
query GetBookBySlug($slug: String!) {
  books(where: { slug: { _eq: $slug } }, limit: 1) { id }
}
query GetSeriesBySlug($slug: String!) {
  series(where: { slug: { _eq: $slug } }, limit: 1) { id }
}
query GetAuthorBySlug($slug: String!) {
  authors(where: { slug: { _eq: $slug } }, limit: 1) { id }
}
```

---

## `GetByIdAsync` — ID & URL Normalisation

`GetByIdAsync` accepts typed IDs and full Hardcover URLs:

| Input | Resolution |
|-------|-----------|
| `hardcover:{n}` | `GetBook(id: n)` |
| `hardcover:series:{n}` | `GetSeries(id: n)` |
| `hardcover:author:{n}` | `GetAuthor(id: n)` |
| `https://hardcover.app/books/{slug}` | `GetBookBySlug` → integer → `GetBook` |
| `https://hardcover.app/series/{slug}` | `GetSeriesBySlug` → integer → `GetSeries` |
| `https://hardcover.app/authors/{slug}` | `GetAuthorBySlug` → integer → `GetAuthor` |

`fixMatchHint` in manifest: *"Paste a Hardcover book, series, or author URL (e.g.
https://hardcover.app/books/the-way-of-kings)"*

---

## `SearchAsync` Cascade

### Level 0 — Author

Two stages, short-circuit at score ≥ 65:

1. Each `AltTitle` → `SearchAuthors(query)`
2. `FilenameStem` if it differs from Name

Scoring: name exact +60, name contains +30, `PreciseName` exact +15, `PreciseName`
partial +5.

### Level 1 — Series

Two stages:

1. Each `AltTitle` → `SearchSeries(query)`
2. If `ParentName` present, append it to the query (author narrows series hits)

Scoring: name exact +60, name contains +30, `primary_books_count` vs `ChildCount` ±10.

### Level 2 — Book (four-stage cascade)

| Stage | Query | Condition |
|-------|-------|-----------|
| 1a | `PreciseName` + year | `PreciseName` present |
| 1b | Each `AltTitle` with year | `Year` present |
| 2a | Each `AltTitle` without year | Always |
| 2b | `FilenameStem` alone | `FilenameStem` ≠ `Name`, prior stages empty |

When `ParentName` is available (author name), it is included in the query string
for stages 1a/1b/2a: `"Title AuthorName"`. If that returns nothing, the search is
retried with title alone before moving to the next stage.

Short-circuit at score ≥ 65. Merge all results if no stage reaches threshold.

#### Scoring signals

| Signal | Points |
|--------|--------|
| Title exact match (normalised, punctuation stripped) | +60 |
| Title contains / is contained | +30 |
| Year exact | +20 |
| Year ±1 | +10 |
| Year mismatch (both sides have a year, it's wrong) | −10 |
| `PreciseName` exact (raw, case-insensitive) | +15 |
| `PreciseName` partial | +5 |
| `ParentName` matches a contribution author name | +20 |
| `ParentName` partially matches | +10 |
| Tiebreaker: `ratings_count` (secondary sort descending) | — |

---

## Field Mapping

### Book / Audiobook (Level 2)

| Hardcover field | Chronicle field |
|-----------------|-----------------|
| `title` | `Title` |
| `description` | `Overview` |
| `release_year` | `Year` |
| `image.url` | `PosterUrl` |
| `contributions[].author.name` where `contribution` ≠ "Narrator" | `Cast` (role = contribution type, e.g. `"Author"`) |
| `default_physical_edition.narrations[].narrator.name` | `Cast` (role = `"Narrator"`) |
| `cached_tags` (genre category) | `Genres` |
| `rating` | `Rating` |
| `default_physical_edition.audio_seconds ÷ 60` | `RuntimeMinutes` (**audiobooks only**) |
| `pages` | `ExtendedData["pages"]` (**books only** — no runtime) |
| `book_series[0].series.name` | `ExtendedData["series_name"]` |
| `book_series[0].position` | `ExtendedData["series_position"]` |
| `book_series[0].series.id` | Cross-ref external ID `hardcover:series:{id}` |
| `book_mappings[].isbn_13` | `ExtendedData["isbn13"]` |
| `book_mappings[].isbn_10` | `ExtendedData["isbn10"]` |

### Series (Level 1)

| Hardcover field | Chronicle field |
|-----------------|-----------------|
| `name` | `Title` |
| `description` | `Overview` |
| First book's `image.url` | `PosterUrl` |
| `is_completed` | `ExtendedData["is_completed"]` |

### Author (Level 0)

| Hardcover field | Chronicle field |
|-----------------|-----------------|
| `name` | `Title` |
| `bio` | `Overview` |
| `image.url` | `PosterUrl` |

---

## RuntimeMinutes — Audiobooks Only

| Source | Value |
|--------|-------|
| File scan (primary) | `TotalDurationSeconds ÷ 60` summed across all audio files by `CollapseAudiobooksToFolders` |
| Hardcover API (fallback, e.g. metadata-only items) | `default_physical_edition.audio_seconds ÷ 60` |
| Books (ebooks / physical) | **Not set.** `RuntimeMinutes = null`. `pages` goes to `ExtendedData["pages"]`. |

---

## Narrator Display — UI

Narrators are cast members with `role = "Narrator"`. They carry the same importance
as the author for audiobooks and must be surfaced prominently.

**`MediaDetailPage` change:** when any cast entry has `role === "Narrator"`, render a
dedicated **Narrator** line directly beneath the title and author line — above the
description, above the full cast grid. Format: `"Narrated by Nick Podehl"` (single)
or `"Narrated by Nick Podehl, Julia Whelan"` (multiple).

The regular cast grid still shows all cast members including narrators, so the data
is visible in two places: prominently at the top, and in the full credits section.

---

## `ImportedWatchEvent` — New Parent Context Fields

`SyncOrchestrationService` needs author/series information to find-or-create the
correct parent MediaItems before matching the Book item. Three optional fields are
added to `ImportedWatchEvent` (and the equivalent rating/watchlist records):

```csharp
string? AuthorName      = null   // e.g. "Brandon Sanderson"
string? SeriesName      = null   // e.g. "The Stormlight Archive"  (null = standalone)
double? SeriesPosition  = null   // e.g. 1.0
```

`HardcoverImportProvider` populates these from the `book_series` and `contributions`
fields when it fetches book data during sync. The orchestrator uses `AuthorName` to
find-or-create the Author MediaItem at Level 0, then `SeriesName` (if present) to
find-or-create the Series MediaItem at Level 1, before matching or creating the Book.

---

## `SyncOrchestrationService` Changes

When processing a `books` or `audiobooks` import event with the new fields:

1. **Author** — match existing Author MediaItem by `hardcover:author:{id}` or by
   name; create stub named `AuthorName` (or `"Unknown"`) at Level 0 if not found
2. **Series** — if `SeriesName` present, match existing Series MediaItem by
   `hardcover:series:{id}` or by name under the Author; create stub if not found
3. **Book** — existing 4-stage matching runs with the Author/Series as the known
   parent context

If `AuthorName` is null, the "Unknown" Author stub is used.

---

## manifest.json Updates

```json
{
  "plugin_id": "hardcover",
  "name": "Hardcover",
  "version": "1.1.0",
  "author": "Michael Beck",
  "description": "Book and audiobook metadata from Hardcover.app, plus reading history import.",
  "entry_type": "Chronicle.Plugin.Hardcover.HardcoverImportProvider",
  "min_chronicle_version": "0.1.0",
  "repository": "https://github.com/thegoddamnbeckster/Chronicle.Plugin.Hardcover",
  "iconUrl": "https://hardcover.app/favicon.ico",
  "brandColorLight": "#8b5cf6",
  "brandColorDark": "#7c3aed",
  "fixMatchHint": "Paste a Hardcover book, series, or author URL (e.g. https://hardcover.app/books/the-way-of-kings)",
  "background_tasks": [
    {
      "task_id": "import-all",
      "display_name": "Import All",
      "description": "Imports full reading history, ratings, and want-to-read list from Hardcover.",
      "default_cron": "0 3 * * *",
      "default_enabled": false
    },
    {
      "task_id": "delta-sync",
      "display_name": "Delta Sync",
      "description": "Imports reading activity added since the last sync.",
      "default_cron": "0 * * * *",
      "default_enabled": true
    }
  ]
}
```

---

## Files Changed

### `Chronicle.Plugin.Hardcover` (existing repo)

| File | Action |
|------|--------|
| `HardcoverMetadataProvider.cs` | **New** — `IMetadataProvider` implementation |
| `HardcoverImportProvider.cs` | Update: `"book"` → `"books"` in all import events; add `AuthorName`/`SeriesName`/`SeriesPosition` fields |
| `HardcoverClient.cs` | Add 6 new query methods (search/get for author, series, book) |
| `HardcoverModels.cs` | Add rich models: `HcAuthor`, `HcSeries`, `HcBookDetail`, `HcNarration`, search result shapes |
| `manifest.json` | Add `brandColor`, `fixMatchHint`; bump version to `1.1.0` |
| `Chronicle.Plugin.Hardcover.csproj` | No changes expected |

### `Chronicle` (main repo)

| File | Action |
|------|--------|
| `Chronicle.Services/FileScanService.cs` | `CollapseAudiobooksToFolders`: sum durations; new `GroupAudiobooksByAuthorAndSeries` step |
| `Chronicle.Services/SyncOrchestrationService.cs` | Handle `AuthorName`/`SeriesName` parent context for books/audiobooks |
| `Chronicle.Plugins/Models/ImportedWatchEvent.cs` | Add `AuthorName?`, `SeriesName?`, `SeriesPosition?` |
| `Chronicle.Plugins/Models/ImportedRating.cs` | Same three fields |
| `Chronicle.Plugins/Models/ImportedWatchlistEntry.cs` | Same three fields |
| `"book"` audit` | All services, controllers, UI code that uses `"book"` as a media type name |
| `Chronicle.Web/…/MediaDetailPage.tsx` | Narrator prominence — dedicated line under title/author |

### `Chronicle.Plugin.MusicBrainz` (existing repo)

| File | Action |
|------|--------|
| `MusicBrainzMetadataProvider.cs` | `audiobooks`: `HierarchyLevels 1→3`, add `HierarchyLabels`, update `SearchAsync` routing |
| `MusicBrainzSearcher.cs` | Add `SearchAudiobookSeriesAsync` (nice-to-have; returns empty list gracefully) |

---

## Acceptance Criteria

1. Scanning an audiobook library produces Author → Series → Book trees in Chronicle
2. Standalone audiobooks (no `AudioGrouping` tag) appear as direct children of the Author node
3. Multi-file audiobooks (e.g., 244 MP3 chapters) are collapsed to one Book item; `RuntimeMinutes` reflects total audio duration
4. `GetByIdAsync("hardcover:12345")` returns full book metadata including narrator in Cast
5. `GetByIdAsync("https://hardcover.app/books/the-way-of-kings")` resolves to the correct book
6. Narrator is displayed prominently on the MediaDetailPage for audiobooks — directly beneath title/author, above description
7. A book with no series is at Level 1 under its Author; it can later be Fix-Matched to a series
8. Author `"Unknown"` is created when no author information is available; the book is reparentable
9. Hardcover inbound sync (import-all / delta-sync) places imported books under the correct Author and Series parents
10. All occurrences of the string `"book"` as a media type identifier have been replaced with `"books"`
11. MusicBrainz audiobook enrichment works at all three levels; missing series is not an error

---

## Out of Scope

- Movie collection hierarchy (separate backlog item: `project_backlog_movies_hierarchy.md`)
- Edition-level book tracking (individual ISBN editions vs canonical works)
- Hardcover community shelves / reading challenges
- Narrator biographies / narrator-as-entity (narrator is a Cast credit only)
- Offline / cached Hardcover data
