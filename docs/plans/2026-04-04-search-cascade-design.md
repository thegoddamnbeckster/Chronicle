# Generic Hierarchical Metadata Search Cascade — Design

**Date:** 2026-04-04
**Status:** Approved

## Problem

The current MusicBrainz and TMDB plugins use ad-hoc, plugin-specific search strategies that:
- Embed year as part of the title string (e.g. search `"(2014) Remixed"`) instead of as a distinct field
- Use parent/artist names as hard Lucene *filter* clauses that over-constrain and miss valid matches
- Have no systematic fallback when confidence is low — stages were added one-off
- Don't use sub-item structure (track listings, episode counts) to disambiguate candidates

## Solution

A four-stage progressive search cascade, generic enough to apply to any hierarchical metadata plugin (MusicBrainz, TMDB, etc.). Each stage accumulates confidence evidence. Stages first try with year, then without year. Plugin authors implement the stages using their API's native capabilities.

---

## Pre-processing

Before any stage, Chronicle extracts from the stored item:

**Year extraction and validation**
- Strip year from title prefix `(2014) Remixed` or suffix `Remixed (2014)`
- Validate range: `1900` to `DateTime.Now.Year + 3`
- Outside range or missing → treat as no year (no boost, no penalty)

**AltTitles list** (ordered by reliability)
1. Stored name with year stripped (e.g. `"Remixed"`)
2. Filename stem, if meaningfully different (e.g. `"Kryptonite"` from `"01 - Kryptonite.mp3"`)
3. Version-qualifier-stripped title, if different from #1 (e.g. `"Kryptonite"` from `"Kryptonite (LP version)"`)

Duplicates are removed. `PreciseName` (NFO-sourced) prepended when present.

**SubItemNames** — names used to validate the candidate match:
- HierarchyLevel 0 (artist/show): names of direct child items (albums, seasons)
- HierarchyLevel 1 (album/season): names of direct child items (tracks, episodes)
- HierarchyLevel 2 (track/episode): names of sibling items (other tracks on same album)

**SubItemMetadata** — structured metadata for leaf-level items, populated progressively:
- Tier 1 (free): track/episode number from filename prefix, disc/season from folder path, title from stem
- Tier 2 (cheap): duration in seconds (±10s tolerance, configurable)
- Tier 3 (expensive, only when earlier tiers don't yield confidence): all file tags (ID3, Vorbis, etc.)

---

## Confidence Scoring

Confidence is **cumulative** across all signals for each candidate. The highest-scoring candidate wins when it exceeds the acceptance threshold (default: 50).

| Signal | Boost |
|--------|-------|
| Exact (phrase) title match | +40 |
| Fuzzy title match | +20 |
| Year match | +20 |
| Sub-item count within tolerance | +15 |
| Each sub-item name matched (normalised, capped at +25) | +5 |
| Each sub-item metadata field matched | +10 |

Year is never searched as part of the title string. It is always a separate API parameter (e.g. `firstreleasedate:2014` for MusicBrainz, `primary_release_year=2014` for TMDB).

---

## Stage 1 — Exact title + artist + year → retry without year

For each AltTitle in priority order:
1. Phrase-quoted search: `"{altTitle}"` + artist + year
2. If no candidate exceeds threshold: retry with same query but no year

Confidence: year match ≤ 60 base; no-year ≤ 40 base.

---

## Stage 2 — Fuzzy title + artist + year → retry without year

For each AltTitle:
1. Fuzzy search: `{altTitle}~` + artist + year
2. Retry without year if needed

Confidence ceiling lower than Stage 1 due to fuzzy title.

---

## Stage 3 — Fuzzy title + artist + year + sub-item list comparison → retry without year

Same fuzzy search as Stage 2, but for each candidate returned, the plugin fetches its sub-item list from the provider and compares:
- **Count** — does the provider's sub-item count match Chronicle's known sub-item count? → +15
- **Names** — normalised name comparison, +5 per match (capped at +25)

Inspect as many candidates as needed. Speed is not a constraint — accuracy is.

Retry all candidates without year if none exceed threshold.

---

## Stage 4 — Fuzzy title + artist + year + sub-item metadata comparison → retry without year

Same search, but fetch **full metadata** for each sub-item from the provider. The provider is the source of truth.

Compare what Chronicle has (from file/scan metadata, Tiers 1-3) against what the provider says the release contains:
- Track/episode number (+10 per match)
- Duration within ±tolerance (+10 per match)
- Normalised title (+10 per match)
- Provider-specific fields: ISRC, air date, etc. (+10 each)

Retry without year if still below threshold.

---

## Once the Top-Level Item is Confirmed

Lock in the match. All sub-item enrichment derives from the confirmed parent ID via direct lookups — no searching. The cascade runs once per hierarchy level, not per item.

---

## Generic Applicability

| Plugin | Stage 3 sub-items | Stage 4 metadata |
|--------|-------------------|------------------|
| MusicBrainz (artist) | Album/release-group list: count + titles | Album year, track count |
| MusicBrainz (album) | Track list: count + titles | Track number, duration, ISRC |
| MusicBrainz (track) | Sibling tracks on same release: count + titles | Track number, disc number, duration |
| TMDB (show) | Season list: count + numbers | Episode count per season, first air date |
| TMDB (season) | Episode list: count + titles | Episode number, air date |

---

## MediaSearchContext Additions

| Field | Type | Purpose |
|-------|------|---------|
| `AltTitles` | `IReadOnlyList<string>?` | Ordered alt title forms to try in each stage |
| `ChildNames` | `IReadOnlyList<string>?` | Names of direct child items for HierarchyLevel 0/1 |
| `SubItemMetadata` | `IReadOnlyList<SiblingInfo>?` | Structured metadata for Stage 4 comparison |

`SiblingNames` remains (HierarchyLevel 2 leaf items use siblings rather than children).

New `SiblingInfo` record:
```csharp
public record SiblingInfo(
    string Name,
    int?   ItemNumber      = null,  // track number, episode number
    int?   DiscNumber      = null,
    int?   DurationSeconds = null,
    IReadOnlyDictionary<string, string>? Tags = null
);
```

---

## Notes

- TMDB plugin source is currently only available in a git worktree (`goofy-nobel`). TMDB cascade redesign is a follow-on task after MusicBrainz is complete and proven.
- Duration tolerance default: 10 seconds. Configurable via app settings key `Enrichment:DurationToleranceSeconds`.
- The existing `ChildCount` field on `MediaSearchContext` remains and feeds Stage 3 count comparison.
