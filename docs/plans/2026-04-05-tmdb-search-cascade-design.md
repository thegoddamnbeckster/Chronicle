# TMDB Search Cascade Design

**Date:** 2026-04-05
**Status:** Approved — pending implementation
**Relates to:** `2026-04-04-search-cascade-design.md` (generic cascade), `2026-03-27-unified-enrichment-design.md`
**Plugin path:** `W:\Scripts\Chronicle.Plugin.TMDB\`

---

## Background

The generic 4-stage search cascade was designed and implemented for MusicBrainz. TMDB requires a parallel design adapted to its API constraints. This document specifies how the cascade maps onto TMDB's search surface and what improvements are needed to `TmdbMetadataProvider.SearchAsync`.

The parent-gating change (child items are not selected for enrichment until their parent is `Completed`) is already in place in `MetadataEnrichmentService` and applies to all plugins including TMDB. This document covers only the plugin-side search improvements.

---

## TMDB Media Type Hierarchy

| HierarchyLevel | Entity | TMDB ExternalId Format | Search Endpoint |
|---|---|---|---|
| 0 | Movie | `movie:{tmdbId}` | `/search/movie` |
| 0 | TV Show | `tv:{tmdbId}` | `/search/tv` |
| 1 | Season | `tv:{showId}/season:{N}` | `/tv/{showId}/season/{N}` (direct lookup, no search) |
| 2 | Episode | `tv:{showId}/season:{S}/episode:{E}` | `/tv/{showId}/season/{S}/episode/{E}` (direct lookup, no search) |

### Key constraint: seasons and episodes are never searched

TMDB does not expose a `/search/season` or `/search/episode` endpoint. Once a show's `tv:{showId}` is confirmed, seasons and episodes are retrieved by direct numeric lookup using the compound ID. The enrichment service already handles this in the TV hierarchy derivation block (`MetadataEnrichmentService.cs` lines ~580–680), and `TmdbMetadataProvider.GetByIdAsync` already supports all four compound ID formats.

This means the cascade only applies meaningfully to **level 0 items** (movies and TV shows). Seasons and episodes are resolved deterministically from the parent's confirmed ID, not by search.

---

## Current SearchAsync — What It Does

`TmdbMetadataProvider.SearchAsync` currently:

- Reads only `context.Name` and `context.Year`
- Calls `/search/movie?query=name&year=year` and/or `/search/tv?query=name&year=year`
- Scores results using only title similarity and year match
- Returns all candidates; enrichment service picks the top scorer

**Fields it ignores entirely:** `AltTitles`, `FilenameStem`, `ChildNames`, `ChildCount`, `SubItemMetadata`, `HierarchyLevel`, `PreciseName` (partially used), `ItemNumber`

---

## Proposed Cascade — Level 0 Only (Movies and TV Shows)

Because seasons/episodes are never searched, the full cascade applies only to root items. The stages differ from MusicBrainz because TMDB has no Lucene syntax and no fuzzy search — it uses a single keyword search endpoint.

### Stage 1 — Exact title, with year

For each title in `AltTitles` (in order):

- Search `/search/movie` or `/search/tv` with `query={title}&year={year}`
- If any result scores above the acceptance threshold → accept, stop

Year is passed as a discrete API parameter (not embedded in the query string). If `context.Year` is null or failed `ValidateYear`, omit the year parameter entirely and proceed to Stage 1b.

**Stage 1b — Exact title, no year**

For each title in `AltTitles`:

- Search without the year parameter
- Accept if score above threshold

### Stage 2 — FilenameStem title variants

If `AltTitles` contains a `FilenameStem` entry that differs from the canonical name, it was already iterated in Stage 1. No separate Stage 2 is needed — `BuildAltTitles` in the enrichment service already produces `[preciseName?, yearStripped, filenameStem?, qualifierStripped?]` in the correct priority order.

This means the TMDB cascade collapses to two stages rather than four:

| Stage | Query | Year |
|---|---|---|
| 1a | Each AltTitle in order | With year |
| 1b | Each AltTitle in order | Without year |

If both stages return zero candidates, the result is `NotFound`.

---

## Scoring Improvements

The current scoring uses only title similarity and year. The following additional signals should be incorporated:

### PreciseName bonus (already partially wired)
- If `context.PreciseName` is set (from an NFO sidecar) and matches the result title exactly → strong bonus (+20)
- This is already in the code but should be verified to apply correctly when `AltTitles` is used

### Episode/season count validation (TV shows only)
- If `context.ChildNames` is populated (list of season names or episode names known from the scanner)
- After getting a TMDB TV show result, fetch `GET /tv/{id}` and compare `number_of_seasons` to `context.ChildCount`
  - Exact match: +15
  - ±1: +5
- This is a post-search validation step, not a search query modification

### Title year disambiguation
- TMDB commonly returns multiple entries for remakes/reboots (e.g., "Dune" 1984 vs 2021)
- Year exact match: +20 (already present — keep)
- Year ±1: +10 (already present — keep)
- Year mismatch > 1: −10 (add this negative signal to push remakes down)

### Popularity tiebreaker
- When two candidates have equal score, prefer the one with higher TMDB `popularity`
- Already implemented for movies; verify it applies to TV results too

---

## AltTitles Integration

`BuildAltTitles` in `MetadataEnrichmentService` already constructs the ordered list:

1. `PreciseName` (if set — from NFO)
2. Year-stripped canonical name (e.g. `"(2019) Chernobyl"` → `"Chernobyl"`)
3. FilenameStem (if different — often cleaner than the tagged title)
4. Version-qualifier-stripped form (e.g. `"Blade Runner (Director's Cut)"` → `"Blade Runner"`)

`SearchAsync` must iterate this list in order rather than using only `context.Name`. Stop at the first stage where any title produces an acceptable result.

---

## What Does Not Apply to TMDB

| MusicBrainz feature | TMDB equivalent |
|---|---|
| Lucene phrase quoting (`MbQuote`) | Not applicable — TMDB uses plain text query strings |
| Stage 3: sub-item name/count scoring | Partially applicable via `ChildCount` post-validation (see above) |
| Stage 4: duration matching | Not applicable — TMDB does not expose episode duration at the search level |
| Artist constraint in every query | Not applicable — movies/shows are root items with no parent |
| `firstreleasedate:` / `date:` Lucene fields | Not applicable — year is a native TMDB search parameter |

---

## Implementation Checklist

The following changes are confined to `Chronicle.Plugin.TMDB\TmdbMetadataProvider.cs` unless noted.

- [ ] **Use `AltTitles` in SearchAsync** — replace the single `context.Name` search with iteration over `context.AltTitles ?? [context.Name]`
- [ ] **Two-stage cascade** — Stage 1a (with year), Stage 1b (without year); stop as soon as a qualifying result is found
- [ ] **Apply `ValidateYear`** — already called by enrichment service before building `MediaSearchContext`; ensure the plugin treats `context.Year == null` as "omit year parameter" rather than passing `year=0`
- [ ] **Child count post-validation** — for TV show results, if `context.ChildCount > 0`, fetch `/tv/{id}` and apply season count scoring bonus before accepting
- [ ] **Year mismatch penalty** — add −10 to results where year differs by more than 1 from `context.Year`
- [ ] **Verify popularity tiebreaker applies to TV** — already present for movies; confirm the sort applies across both result sets when both movie and TV are searched simultaneously

---

## What the Enrichment Service Already Handles (No Plugin Changes Needed)

- **Parent-gating** — seasons/episodes are not selected until the show is `Completed`
- **TV hierarchy ID derivation** — `tv:{showId}/season:{N}/episode:{E}` compound IDs are constructed in `MetadataEnrichmentService` before `GetByIdAsync` is called; `SearchAsync` is never called for seasons or episodes
- **NFO sidecar TMDB ID** — `TryReadNfoTmdbId` reads `tvshow.nfo` / `movie.nfo` and constructs a direct `GetByIdAsync` call, bypassing `SearchAsync` entirely for items that have NFO files
- **HierarchyLevel ordering** — enrichment batch processes level 0 before level 1 before level 2

---

## Acceptance Criteria

1. A TV show with a common name (e.g. "House") and a year set in the scanner is matched to the correct TMDB entry, not a different show with the same name
2. A movie with a year-prefixed folder name (e.g. `(2021) Dune`) is matched to the 2021 version, not the 1984 version
3. A TV show with an alternate folder name (file stem differs from canonical title) is still found using the `AltTitles` fallback
4. Seasons and episodes continue to be resolved by direct ID lookup — `SearchAsync` is not called for them
5. A show or movie that genuinely does not exist on TMDB is marked `NotFound` after both stages, not left as `Pending`
