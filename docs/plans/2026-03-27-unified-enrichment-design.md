# Unified Metadata Enrichment — Design

**Date:** 2026-03-27
**Status:** Approved, pending implementation plan
**Replaces:** `MetadataRefreshService`, `MetadataEnrichmentService` (current split design)

---

## Problem

Chronicle has two independent systems that both do metadata enrichment:

| | `MetadataEnrichmentService` | `MetadataRefreshService` |
|---|---|---|
| Triggered by | Background (`fetch-missing-metadata`) | User (Refresh / Fix Match), scheduled (`resync-all-metadata`) |
| External ID source | `enrichment_statuses.ExternalId` | `media_external_ids` table |
| Updates | `MetadataJson`, `PosterUrl` | `MetadataJson`, `PosterUrl`, Name, Year, Overview, Runtime |
| State tracking | `enrichment_statuses` | `media_item_refresh_logs` |
| TV hierarchy | Compound IDs constructed in service | `ITvDetailProvider` calls in service |
| Search query construction | Built in service (knows Lucene syntax) | Built in service |
| Confidence scoring | In service | No scoring — takes first result |

These systems are out of sync by design. `media_external_ids` and `enrichment_statuses.ExternalId` are separate stores that diverge whenever Fix Match or a per-plugin refresh runs without updating the other. The result: the background enrichment task re-searches by name on items the user has already manually corrected, silently overwriting their fix overnight.

Additional problems:
- First-class field updates (Name, Year, Overview, Runtime) only happen on the refresh path, not the enrichment path
- TV/music hierarchy logic is in the service, not the plugin — the service knows Lucene query syntax and TMDB URL patterns
- No confidence threshold on the refresh path — first search result always wins
- `OperationCanceledException` from HTTP timeouts propagates and kills the entire background batch

---

## Design

### Principle

One entry point. One table. One code path. The caller passes in what it wants; the service figures out how to satisfy it. Every item at every hierarchy level is treated identically.

### Data Model

Drop `enrichment_statuses`, `media_external_ids`, and `media_item_refresh_logs`. Replace with a single `media_enrichment` table:

| Column | Type | Notes |
|---|---|---|
| `id` | PK | |
| `media_item_id` | FK → `media_items` | |
| `plugin_id` | string | e.g. `chronicle.plugin.tmdb` |
| `external_id` | string? | null until matched; single authoritative ID per item+plugin |
| `status` | enum | Pending / Completed / Failed / Exhausted / NotFound / Skipped |
| `retry_count` | int | |
| `max_retries` | int | |
| `last_attempted_at` | datetime? | |
| `last_completed_at` | datetime? | replaces refresh log "last refreshed" display |
| `error_message` | string? | |
| `diagnostics_json` | string? | candidates, scores, signals used, threshold at time of match |

Every item at every hierarchy level — show, season, episode, artist, album, track, movie — has one row per plugin. No special cases.

### EnrichmentOptions

The caller's intent is expressed as an options object passed into the service:

```csharp
public record EnrichmentOptions(
    EnrichmentMode Mode,
    string?        IdOverride = null,  // Fix Match: user-supplied external ID
    bool           Cascade    = true   // recurse into children after enriching self
);

public enum EnrichmentMode
{
    FillGaps,  // skip Completed items — background task behaviour
    Force      // always re-fetch — user-triggered refresh behaviour
}
```

### Service Interface

`IMetadataRefreshService` is deleted. `IMetadataEnrichmentService` expands to cover all cases:

```csharp
public interface IMetadataEnrichmentService
{
    // ── Main entry point ─────────────────────────────────────────────────────
    // All callers — user, background, Fix Match — use one of these two.

    /// Enrich one item for one plugin, then cascade to children per options.
    Task EnrichItemAsync(int mediaItemId, string pluginId,
                         EnrichmentOptions options, CancellationToken ct = default);

    /// Enrich one item across ALL applicable plugins (e.g. "Refresh All" button).
    Task EnrichItemAsync(int mediaItemId,
                         EnrichmentOptions options, CancellationToken ct = default);

    // ── Background task entry points ─────────────────────────────────────────
    Task EnrichPendingAsync(string pluginId, CancellationToken ct = default);
    Task EnrichAllAsync(CancellationToken ct = default);

    // ── Row management ────────────────────────────────────────────────────────
    Task ResetAsync(string pluginId, ResetScope scope,
                    int? mediaItemId = null, CancellationToken ct = default);
    Task SkipAsync(int mediaItemId, string pluginId, CancellationToken ct = default);

    // ── UI data ───────────────────────────────────────────────────────────────
    Task<IReadOnlyList<EnrichmentStats>>   GetStatsAsync(CancellationToken ct = default);
    Task<PagedEnrichmentItems>             GetItemsAsync(string pluginId, string? status,
                                               int page, int pageSize, string? search,
                                               CancellationToken ct);
    Task<IReadOnlyList<EnrichmentRecord>>  GetEnrichmentRecordsAsync(int mediaItemId,
                                               CancellationToken ct = default);
}
```

**Caller mapping:**

| Old call | New call |
|---|---|
| `RefreshItemAsync(id)` | `EnrichItemAsync(id, new EnrichmentOptions(Force))` |
| `RefreshItemForPluginAsync(id, pluginId, null)` | `EnrichItemAsync(id, pluginId, new EnrichmentOptions(Force))` |
| `RefreshItemForPluginAsync(id, pluginId, input)` | `EnrichItemAsync(id, pluginId, new EnrichmentOptions(Force, IdOverride: input, Cascade: false))` |
| `RefreshForPluginAsync(pluginId)` | loop library roots → `EnrichItemAsync(id, pluginId, Force)` |
| `EnrichPendingAsync(pluginId)` | same name, same behaviour |

### Core Logic: EnrichItemCoreAsync

Single private method. Handles every item, every level, every provider:

```
EnrichItemCoreAsync(item, pluginId, options, db):

  1. LOAD enrichment row for item+plugin (create Pending row if missing)

  2. MODE CHECK
     If FillGaps AND status == Completed AND no IdOverride → skip, return

  3. RESOLVE ID  (in priority order)

     a. IdOverride supplied → use it directly, proceed to step 5

     b. enrichment.ExternalId already set → validate type prefix matches
        hierarchy level (e.g. bare "tv:314" on a season row is stale)
        If valid → proceed to step 5
        If stale → clear it, fall through to (c)

     c. item.ParentId != null AND parent enrichment is Completed
        → construct derived ID: parent.ExternalId + "/" + itemType + ":" + item.Number
        → call provider.GetByIdAsync(derived_id)
        → if returns data → accept, no scoring (confidence inherited from parent)
        → if returns null/404 → fall through to (d)

     d. Root item with no stored ID
        → call plugin.SearchAsync(context) — plugin constructs its own query
        → apply confidence threshold (see Scoring section)

  4. If no ID resolved after all steps → status = NotFound, store diagnostics, return

  5. FETCH full metadata
     provider.GetByIdAsync(resolvedId)
     The full provider response is stored — nothing is discarded.
     Plugins must populate ExtendedData with any fields not covered by
     first-class MediaMetadata properties. Chronicle is the system of record
     for all metadata the provider returns.

  6. MERGE into item
     MetadataJson[pluginId] = complete serialised provider response
     (Results/TotalResults excluded — search-index fields, not entity data)
     First-class fields updated:
       PosterUrl, Overview, RuntimeMinutes — always from provider if present
       Name, Year — updated for root items (HierarchyLevel == 0) only;
                    child names like "Season 02" are not overwritten unless
                    IsGenericName(item.Name) == true

  7. UPDATE enrichment row
     ExternalId = resolvedId
     Status = Completed, LastCompletedAt = now, ErrorMessage = null
     DiagnosticsJson updated with signals used

  8. CASCADE  (if options.Cascade == true)
     Load direct children of this item, ordered by Number
     For each child:
       EnrichItemCoreAsync(child, pluginId,
           options with { IdOverride = null }, db)
     One failed child does not stop siblings — exceptions caught per child

  ERROR HANDLING
     Any exception in steps 3–6 is caught at the item level.
     HttpClient timeouts (TaskCanceledException with internal token) are
     caught here — NOT rethrown. Item gets Failed/Exhausted, batch continues.
     Only ct.IsCancellationRequested triggers a rethrow.
```

### Confidence Scoring

Applies only at step 3d (root items with no stored ID). The plugin does the searching and scoring; Chronicle applies the threshold.

**Plugin interface** (`IMetadataProvider` in `Chronicle.Plugins`):

```csharp
public record MediaSearchContext(
    string  Name,
    int?    Year,
    string? ParentName,       // artist for album, show for season
    string? GrandparentName,  // artist for track
    int?    ItemNumber,        // season/track/episode number
    int?    ChildCount,        // seasons under show, tracks on album — structural check
    int     HierarchyLevel    // 0 = root, 1 = season/album, 2 = episode/track
);

public record ScoredCandidate(
    MediaMetadata Metadata,
    int           Score,       // 0–100, plugin-computed
    string?       ScoreReason  // human-readable explanation of signals fired
);

// Replaces: Task<MediaMetadata> SearchAsync(string query)
Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
    MediaSearchContext context, CancellationToken ct);
```

The plugin owns query construction (Lucene syntax for MusicBrainz, text search for TMDB), candidate retrieval, and scoring. Chronicle only decides accept/reject:

- Score ≥ threshold → accept top candidate
- Score < threshold → NotFound, store all candidates in diagnostics

**Default threshold:** 50/100, configurable via `app_settings` key `enrichment.confidence_threshold`.

**Name normalisation** (Chronicle-side, applied before passing context to plugin and for display):
- Strip punctuation: `:` `,` `-` `'` `.`
- Strip leading articles: `"The "` `"A "` `"An "`
- Lowercase

This ensures `"Star Trek, Enterprise"` reaches the plugin as `"star trek enterprise"` and matches `"Star Trek: Enterprise"` without the plugin needing to handle filesystem naming conventions.

**Diagnostics stored regardless of outcome:**
- Normalised name used
- Top 5 candidates with scores and reasons
- Threshold value at time of attempt
- Child count used (if structural signal fired)

### ITvDetailProvider Retired

`ITvDetailProvider.GetTvSeasonAsync` / `GetTvEpisodeAsync` are Chronicle-side interfaces that let the service call TMDB season/episode APIs directly. In the new model the service calls `GetByIdAsync("tv:314/season:2")` and the TMDB plugin handles that compound ID internally. TV-specific API logic moves entirely inside the plugin. `ITvDetailProvider` is deleted.

---

## Migration

### Database

1. EF migration: create `media_enrichment` table
2. Data migration script: merge `enrichment_statuses` rows into `media_enrichment`; join `media_external_ids` rows to populate `external_id` column where available
3. EF migration: drop `enrichment_statuses`, `media_external_ids`, `media_item_refresh_logs`

### Services

- Delete `MetadataRefreshService.cs` and `IMetadataRefreshService.cs`
- Expand `MetadataEnrichmentService` with `EnrichItemCoreAsync` and cascade logic
- Update `PluginTaskRunner`: `resync-all-metadata` → `EnrichItemAsync(Force)` loop

### Plugins

- Update `IMetadataProvider.SearchAsync` signature in `Chronicle.Plugins`
- Update TMDB plugin: implement `SearchAsync(MediaSearchContext)`, remove `ITvDetailProvider`
- Update MusicBrainz plugin: implement `SearchAsync(MediaSearchContext)` (move Lucene query construction from service into plugin)

### API

No endpoint shape changes. Controllers swap `IMetadataRefreshService` injection for `IMetadataEnrichmentService`. Frontend requires no changes.

### Tests

- Delete unit tests for `MetadataRefreshService`
- Update enrichment unit tests for new interface and cascade behaviour
- Add tests: confidence threshold accept/reject, FillGaps skip, Force re-fetch, cascade stops at correct depth, HTTP timeout caught per-item

---

## What This Fixes

- **Enterprise overnight revert** — single ID store means Fix Match and background enrichment can never diverge
- **Metadata disappears on refresh** — first-class fields now updated on the enrichment path; no separate refresh path to miss
- **Background batch killed by timeout** — `OperationCanceledException` from HTTP timeouts caught per-item
- **Wrong search results accepted silently** — confidence threshold rejects poor matches and stores diagnostics
- **Service knows Lucene syntax** — query construction moves into plugins
- **`ITvDetailProvider` leaking TV concepts into Chronicle core** — retired; plugin handles internally
