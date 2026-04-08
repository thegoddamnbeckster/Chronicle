# Chronicle.Plugin.OpenLibrary

Metadata source plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) that
fetches book and audiobook metadata from [Open Library](https://openlibrary.org/).

**Plugin ID:** `chronicle.plugin.openlibrary`
**Version:** 1.0.0
**Media Types:** Books (`books`), Audiobooks (`audiobooks`)
**Auth:** None — fully public API, no key required
**API:** Open Library REST API — `https://openlibrary.org`

---

## Table of Contents

- [Overview](#overview)
- [Supported Media Types](#supported-media-types)
- [Settings Schema](#settings-schema)
- [API Endpoints](#api-endpoints)
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

[Open Library](https://openlibrary.org/) is an Internet Archive project providing free,
open access to book metadata for millions of titles. Its data is licensed **CC0 (public
domain)** — fully open for any use without attribution requirements.

Key advantages over other book metadata sources:
- **No API key required** — zero setup, install and enable immediately
- **CC0 licensed** — no legal risk for any use
- Covers available for most books via the Covers API
- Cross-references to GoodReads IDs, LibraryThing, ISBNs, OCLC, LCCN, etc.
- Maintained by the Internet Archive — not going away

Known gaps: descriptions absent for many older or obscure titles; series data
inconsistently populated; no ratings or reviews data.

---

## Supported Media Types

| Media Type | HierarchyLevel | Priority | Notes |
|------------|----------------|----------|-------|
| `books` | 0 | 2 | All books — Works (canonical across editions) |
| `audiobooks` | 0 | 2 | Audiobooks — same data model as books |

Books and audiobooks are flat HierarchyLevel 0 items. Series membership is stored as
metadata; no series container hierarchy is created.

---

## Settings Schema

No authentication is required. Settings are optional quality-of-life knobs.

| Key | Label | Type | Required | Default | Notes |
|-----|-------|------|----------|---------|-------|
| `MaxRetries` | Max Retries | Number | No | `3` | Per-item failure limit before `Exhausted` |
| `RateLimitMs` | Rate Limit (ms) | Number | No | `1000` | ms between requests; OL asks for ≤1 req/sec |
| `CoverSize` | Cover Size | Dropdown | No | `L` | `S` (small), `M` (medium), `L` (large) |
| `UserAgent` | User-Agent | Text | No | `Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)` | Identify your app in requests |

> Users can install and use this plugin with **zero configuration** — just install and enable.

---

## API Endpoints

| Operation | Endpoint |
|-----------|---------|
| Search books | `GET /search.json?title={t}&author={a}&fields=...&limit=10` |
| Work by OLID | `GET /works/{olid}.json` |
| Edition by OLID | `GET /books/{olid}.json` |
| Author by OLID | `GET /authors/{olid}.json` |
| ISBN lookup | `GET /isbn/{isbn}.json` (redirects to edition) |
| Multi-ISBN lookup | `GET /api/books?bibkeys=ISBN:{isbn}&format=json&jscmd=data` |
| GoodReads ID lookup | `GET /search.json?q=identifiers.goodreads%3A{id}&limit=1` |
| Cover by cover ID | `GET https://covers.openlibrary.org/b/id/{id}-{size}.jpg` |
| Cover by ISBN | `GET https://covers.openlibrary.org/b/isbn/{isbn}-{size}.jpg` |
| Cover by OLID | `GET https://covers.openlibrary.org/b/olid/{olid}-{size}.jpg` |
| Health check | `GET /works/OL45804W.json` (known-good work) |

### Search Response Fields

The `search.json` endpoint returns a flat document per match:

```
key (Work OLID), title, author_name[], author_key[],
first_publish_year, isbn[], cover_i, subject[],
number_of_pages_median, id_goodreads[], id_librarything[],
publisher[], language[], edition_count, series[]
```

### Work Detail Fields

`/works/{olid}.json` returns:

```
title, description (plain string OR {type, value} object),
subjects[], subject_places[], subject_times[], subject_people[],
authors[{author.key}], first_publish_date, covers[],
links[{title, url}], created, last_modified, series[]
```

> **Important:** `description` can be either a plain string or a `{ "type": "/type/text", "value": "..." }` object. The implementation must handle both cases.

### Author Detail Fields

`/authors/{olid}.json` returns:

```
name, bio (plain string or {value} object), birth_date, death_date,
photos[], links[{title, url}], alternate_names[], created, last_modified
```

---

## External ID Format

| Entity | Format | Example |
|--------|--------|---------|
| Work (canonical) | `work:{olid}` | `work:OL45804W` |
| Edition | `edition:{olid}` | `edition:OL6990157M` |
| Author | `author:{olid}` | `author:OL1394865A` |

Always store the **Work OLID** as the canonical external ID (keyed as `work:{OL…W}`).
Edition OLIDs are used during lookup but not stored as the primary identifier.

---

## Fix Match Resolution

`fixMatchHint`: *Enter an Open Library URL (e.g. https://openlibrary.org/works/OL45804W) or a bare OLID (e.g. OL45804W), or a GoodReads URL (e.g. https://www.goodreads.com/book/show/186074)*

| Input | Resolution |
|-------|-----------|
| `https://openlibrary.org/works/{olid}` | Parse OLID → `GET /works/{olid}.json` |
| `https://openlibrary.org/works/{olid}/{title_slug}` | Parse OLID from path |
| `work:{olid}` | Direct → `GET /works/{olid}.json` |
| Bare OLID (`OL45804W`) | Treat as `work:{olid}` |
| `isbn:{isbn13}` / `isbn10:{isbn10}` | `GET /isbn/{isbn}.json` → resolve to Work |
| 10 or 13 digit number | Treat as ISBN |
| `https://www.goodreads.com/book/show/{id}` | Extract GoodReads ID → `/search.json?q=identifiers.goodreads:{id}` |
| `https://www.goodreads.com/book/show/{id}.{slug}` | Extract numeric ID only |

---

## Search Strategy

### Stage 1a — AltTitles with year

For each title in `context.AltTitles` (PreciseName → year-stripped → filenameStem → qualifier-stripped):

1. `GET /search.json?title={title}&author={parentName}&fields=key,title,author_name,...&limit=10`
   (`author` omitted if `context.ParentName` is null)
2. Score results (see table below); year comparison at scoring time (no native year query param)
3. Accept if top candidate scores ≥ 50 — stop

### Stage 1b — AltTitles without year

Same iteration, year signals excluded from scoring. Accept if score ≥ 50.

If both stages return zero candidates → `NotFound`.

### ISBN Short-Circuit

If `fileScannerMetadata` or file tags contain ISBN-13 or ISBN-10:

1. `GET /isbn/{isbn}.json` → follow redirect to edition OLID
2. Fetch edition → extract `works[0].key`
3. `GET /works/{olid}.json` — confirmed result, threshold not applied

### GoodReads Cross-Reference Short-Circuit

If the user has entered a GoodReads URL or the scanner has stored a GoodReads ID:

1. `GET /search.json?q=identifiers.goodreads:{id}&limit=1`
2. If work found → confirmed result, threshold not applied

### Scoring Signals

| Signal | Score |
|--------|-------|
| Exact title match (normalised, lower) | +40 |
| Fuzzy title match (Levenshtein ≤ 20%) | +20 |
| First publish year exact match | +20 |
| First publish year ±1 | +10 |
| Year mismatch > 1 | −10 |
| Author name match (`context.ParentName`) | +15 |
| Series name partial match | +5 |
| ISBN / GoodReads ID cross-reference | auto-accept |

Default acceptance threshold: **50**

---

## GetByIdAsync

| Input format | Behaviour |
|---|---|
| `work:{olid}` | `GET /works/{olid}.json` |
| `edition:{olid}` | `GET /books/{olid}.json` → resolve to work |
| `author:{olid}` | `GET /authors/{olid}.json` |
| `isbn:{isbn13}` | `GET /isbn/{isbn13}.json` → resolve to work |
| `isbn10:{isbn10}` | Same, ISBN-10 variant |
| `goodreads:{id}` | GoodReads cross-reference search → resolve to work OLID |
| Bare OLID (`OL{n}W`) | Treat as `work:{olid}` |

---

## `metadata_json` Storage

All data stored under the full plugin ID key:

```json
{
  "chronicle.plugin.openlibrary": {
    "olid": "OL45804W",
    "title": "The Name of the Wind",
    "description": "Told in Kvothe's own voice, this is the tale of the magically gifted young man...",
    "first_publish_year": 2007,
    "subjects": ["Fantasy fiction", "Magic", "Epic fantasy", "Coming of age"],
    "subject_places": [],
    "subject_times": [],
    "subject_people": ["Kvothe"],
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
    "series": ["The Kingkiller Chronicle"],
    "isbn": ["9780756404734", "9780756404079"],
    "id_goodreads": ["186074"],
    "id_librarything": ["3869"],
    "links": [
      { "title": "Author's Website", "url": "https://patrickrothfuss.com" }
    ]
  }
}
```

### First-Class Chronicle Field Mappings

| `metadata_json` field | Chronicle field |
|---|---|
| `title` | `media_items.name` |
| `first_publish_year` | `media_items.year` |
| `cover_url` | Poster image |
| `description` | PluginMetadataBox overview |
| `authors[0].name` | Primary creator display |
| `isbn[0]` | `media_external_ids` |
| `id_goodreads[0]` | `media_external_ids` (source `goodreads`) |

---

## Rate Limiting

`SemaphoreSlim(1,1)` + `Stopwatch` elapsed guard — same pattern as MusicBrainz.

- Default gap: 1000ms (1 req/sec, as requested by Open Library)
- Applies to ALL outbound calls (search, work fetch, author fetch, image fetch)
- `RateLimitMs` setting overrides at configure time; floor is 500ms (hard-coded)
- Cover image downloads do NOT count against the rate limit (served from CDN)

---

## Background Tasks

| Task | Schedule | Enabled by default |
|------|----------|-------------------|
| `fetch-missing-metadata` | Daily at 04:00 | Yes |
| `resync-all-metadata` | Weekly Sunday 03:00 | No |

---

## Repository Structure

```
W:\Scripts\Chronicle.Plugin.OpenLibrary\
├── Chronicle.Plugin.OpenLibrary.csproj
├── manifest.json
├── OpenLibraryMetadataProvider.cs    # IMetadataProvider implementation
├── OpenLibraryClient.cs              # REST HTTP client + rate limiter
├── OpenLibrarySearcher.cs            # SearchAsync cascade, AltTitles iteration, ISBN short-circuit
├── OpenLibraryModels.cs              # C# models for JSON deserialization
├── OpenLibraryMetadataMapper.cs      # OpenLibraryWork → MediaMetadata + metadata_json
└── tests/
    ├── OpenLibrarySearcherTests.cs
    ├── OpenLibraryMetadataMapperTests.cs
    └── OpenLibraryClientTests.cs
```

---

## Building & Packaging

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>Chronicle.Plugin.OpenLibrary</AssemblyName>
    <RootNamespace>Chronicle.Plugin.OpenLibrary</RootNamespace>
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

Deploy output to: `plugins/openlibrary/` under the Chronicle API publish directory.

---

## Implementation Notes

- `description` field from Works API can be a plain `string` OR `{ "type": "/type/text", "value": "..." }` object — deserialise as `JsonElement` and detect which form it is
- `subjects` can be hundreds of items on popular books — limit display to top 10, store all
- `first_sentence` on a Work is sometimes better than `description` for short overviews — store both, prefer `description` if present
- `covers[]` is an array of integer IDs; build URL as `covers.openlibrary.org/b/id/{id}-L.jpg`; use the first one that returns HTTP 200 (some IDs are defunct)
- For the `authors` array on a Work, each entry is `{ "author": { "key": "/authors/OL{n}A" }, "type": ... }` — requires a separate fetch per author to get name and bio
- Store the Work OLID in `media_external_ids` with source `openlibrary` as `work:OL{n}W`

---

## Acceptance Criteria

1. `work:OL45804W` resolves via `GetByIdAsync` — all available fields stored
2. ISBN-13 in file scanner metadata bypasses search and resolves directly
3. GoodReads URL in Fix Match resolves to correct Open Library work
4. Title + author search returns correct result at score ≥ 50
5. Year-prefixed folder name matched via Stage 1b fallback
6. Item absent from Open Library → `NotFound` (not `Exhausted`)
7. Cover image downloaded and stored
8. Rate limiting stays ≤ 1 req/sec

---

*Chronicle.Plugin.OpenLibrary is an independent community plugin and is not affiliated with
or endorsed by the Internet Archive or Open Library.*
