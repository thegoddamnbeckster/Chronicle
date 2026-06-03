# Metadata Assignment Enforcement — Design Document

**Date:** 2026-06-03
**Status:** Approved, ready for implementation
**Bug:** BUG-040 — metadata assignment config is stored but never enforced

---

## Problem

The Metadata Assignment page (Settings → Metadata Assignment) lets the user configure per-field plugin priority (e.g. Hardcover first, MusicBrainz second for all audiobook fields). This config is stored correctly in `app_settings` under `metadata_assignment.config`. However, `MetadataEnrichmentService` never reads it. Both `MergeMetadata` and `MergeProviderResult` blindly overwrite first-class columns (`Name`, `Year`, `Overview`, `PosterUrl`, `RuntimeMinutes`) whenever a plugin returns a non-empty value. The last plugin to run wins — regardless of configured priority.

Chronicle is also intended to serve as a metadata source for external applications. Those consumers need one authoritative, pre-resolved answer per field rather than having to implement their own priority logic against raw per-plugin blobs.

---

## Goals

1. Enforce the assignment config so the highest-priority plugin's value always wins for each field.
2. Expose a `resolvedMetadata` object in the API response covering all assignable fields (including non-first-class ones like `rating`, `genres`, `cast`).
3. Keep `_resolved` in sync automatically — no manual user action ever required.
4. Process large collections without unbounded RAM usage.

---

## Design

### 1. `_resolved` in `metadata_json`

A new `"_resolved"` key is added to each item's `metadata_json` blob alongside the existing per-plugin keys:

```json
{
  "hardcover": { "title": "The Way of Kings", "posterUrl": "https://...", "rating": 4.5, ... },
  "chronicle.plugin.musicbrainz": { "title": "The Way of Kings", "posterUrl": null, ... },
  "_resolved": {
    "title": "The Way of Kings",
    "overview": "Roshar is a world of stone and storms...",
    "posterUrl": "https://...",
    "year": 2010,
    "runtimeMinutes": 2709,
    "backdropUrl": null,
    "rating": 4.5,
    "genres": ["Fantasy", "Epic"],
    "cast": ["Author:Brandon Sanderson", "Narrator:Michael Kramer"],
    "directors": [],
    "tags": ["epic fantasy", "magic system"]
  }
}
```

`_resolved` is computed by the resolution algorithm and is the single authoritative view of an item's metadata. All other plugin keys remain untouched — nothing is lost.

---

### 2. Resolution Algorithm

For a given `MediaItem`:

1. Determine the assignment config key: `"{mediaTypeName}.{hierarchyLevel}"` for level > 0, or `"{mediaTypeName}"` for level 0. Example: `"audiobooks.2"` for a Book under a Series.
2. Load the priority list for each field from the config. If no config exists for this media type, fall back to iterating plugins in registry order (same behaviour as today — no regression).
3. For each assignable field, walk the priority list top to bottom. Read the value from `metadata_json[pluginId][camelCaseFieldName]`. Take the first non-null, non-empty value.
4. If no plugin has a value for a field, that field is **absent** from `_resolved` (not null, not empty string — absent).
5. Write `_resolved` back into `metadata_json`.
6. Promote the 5 first-class columns from `_resolved`: `Name` ← `title`, `Year` ← `year`, `Overview` ← `overview`, `PosterUrl` ← `posterUrl`, `RuntimeMinutes` ← `runtimeMinutes`. If `_resolved` has no value for a field, the first-class column is left unchanged (not cleared).

**Field name mapping** (assignment config snake_case → `metadata_json` camelCase):

| Config key         | JSON key          | First-class column   |
|--------------------|-------------------|----------------------|
| `title`            | `title`           | `Name` (level 0 only)|
| `overview`         | `overview`        | `Overview`           |
| `year`             | `year`            | `Year` (level 0 only)|
| `poster_url`       | `posterUrl`       | `PosterUrl`          |
| `backdrop_url`     | `backdropUrl`     | —                    |
| `runtime_minutes`  | `runtimeMinutes`  | `RuntimeMinutes`     |
| `rating`           | `rating`          | —                    |
| `genres`           | `genres`          | —                    |
| `cast`             | `cast`            | —                    |
| `directors`        | `directors`       | —                    |
| `tags`             | `tags`            | —                    |

`title` and `year` are only promoted to `Name`/`Year` at hierarchy level 0 (same rule as today).

---

### 3. When `_resolved` Is Recomputed

**On every enrichment write:** At the end of `MergeMetadata` / `MergeProviderResult`, `MetadataResolutionService.ResolveAsync` is called for that item. Always up to date after any enrichment run, Fix Match, or import.

**On assignment config change:** When `PUT /api/v1/settings/metadata-assignment` is saved, the controller fires a background `Task.Run` that calls `MetadataResolutionService.ResolveAllForMediaTypeAsync` for every media type whose assignment changed. This re-walks all affected items using only already-stored `metadata_json` data — no network calls. The user sees no prompt and takes no action; the data simply updates.

---

### 4. Service Structure

**`MetadataResolutionService`** (new, in `Chronicle.Services`):

```csharp
public interface IMetadataResolutionService
{
    /// Recomputes _resolved for a single item and promotes first-class columns.
    Task ResolveAsync(MediaItem item, ChronicleDbContext db, CancellationToken ct);

    /// Bulk recompute for all items of a given media type. Processes in batches of 100.
    Task ResolveAllForMediaTypeAsync(string mediaTypeName, CancellationToken ct);
}
```

**`AssignmentConfigCache`** (new, singleton, in `Chronicle.Services`):
- Holds the parsed assignment config in memory. Tiny — a few KB regardless of library size.
- Populated on first use, invalidated when the settings controller saves a new config.
- Falls back to a DB read if the cache is cold.

**Batch safety:** `ResolveAllForMediaTypeAsync` uses keyset pagination (not `Skip/Take`) to stream through items 100 at a time. Each batch is loaded, resolved, saved, and released before the next batch is fetched. Memory usage is bounded to ~100 items at any time.

---

### 5. API Response

`MediaItemDto` gains a `ResolvedMetadata` property:

```csharp
public ResolvedMetadataDto? ResolvedMetadata { get; set; }
```

```csharp
public record ResolvedMetadataDto(
    string?        Title,
    string?        Overview,
    int?           Year,
    string?        PosterUrl,
    string?        BackdropUrl,
    int?           RuntimeMinutes,
    double?        Rating,
    List<string>?  Genres,
    List<string>?  Cast,
    List<string>?  Directors,
    List<string>?  Tags
);
```

`ResolvedMetadata` is `null` when `_resolved` is absent from `metadata_json` (item has never been enriched). It is populated from `_resolved` in the media item query — no extra DB round-trip, just a JSON deserialize of the already-loaded `MetadataJson`.

The existing first-class fields (`title`, `posterUrl`, `overview`, etc.) at the top level of the response remain unchanged for backward compatibility. `resolvedMetadata` is additive.

---

### 6. What Is Not Changing

- Per-plugin blobs in `metadata_json` are never modified or removed. All raw data is preserved.
- The `PluginMetadataBox` UI components continue to read from their per-plugin blob — no frontend changes needed for existing functionality.
- Enrichment scheduling, retry logic, and status tracking are unchanged.
- The Metadata Assignment UI page is unchanged — it already writes the config correctly.

---

## Testing

- **Unit tests** for `MetadataResolutionService.ResolveAsync`: verify priority waterfall (first non-empty wins), absent fields, no-config fallback, first-class column promotion rules.
- **Unit test** for `AssignmentConfigCache`: verify invalidation on config save.
- **Integration test**: enrich an item with two plugins → verify `_resolved` and first-class columns reflect the higher-priority plugin's values.
- **Integration test**: save new assignment config → verify background recompute fires and `_resolved` updates without re-enrichment.
