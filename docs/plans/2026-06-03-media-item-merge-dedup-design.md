# Media Item Merge & Deduplication — Design Document

**Date:** 2026-06-03
**Status:** Approved, ready for implementation

---

## Problem

Duplicate `MediaItem` records accumulate from multiple sources: file scanner, inbound sync (Hardcover, Trakt, SIMKL), and metadata enrichment. These duplicates have slightly different names (e.g. "James S. A. Corey" vs "James S.A. Corey") and divergent metadata. The existing `DuplicateCleanupService` handles definite matches (shared external ID) automatically but cannot surface probable matches for human review, has no UI, and has no unmerge capability.

This design adds:
- A candidate detection system for near-identical names
- Manual merge with side-by-side winner selection
- Lossless merge with AKA preservation
- Structural unmerge with automatic re-enrichment
- Applies to all media types and hierarchy levels

---

## Decisions

| Question | Answer |
|---|---|
| How duplicates surfaced? | Settings → Duplicates page + "Merge with…" on media detail page |
| AKA storage | `media_item_aliases` table, indexed, included in global search |
| Unmerge scope | Structural: external IDs + children restored; library/events stay on winner |
| Manual winner selection | User chooses explicitly in side-by-side modal |
| Auto-detection threshold | Definite (shared external ID, existing) + Probable (normalized name match) |

---

## Data Model

### New table: `media_item_aliases`

```sql
CREATE TABLE media_item_aliases (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    media_item_id INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
    alias         TEXT    NOT NULL,
    source        TEXT    NOT NULL,   -- e.g. "merge", "plugin:hardcover"
    created_at    DATETIME NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_aliases_media_item_id ON media_item_aliases(media_item_id);
CREATE INDEX idx_aliases_alias ON media_item_aliases(alias);  -- for search
```

### New table: `media_item_merges`

```sql
CREATE TABLE media_item_merges (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    winner_id             INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
    loser_original_id     INTEGER NOT NULL,   -- former ID, item is deleted
    loser_name            TEXT    NOT NULL,
    loser_media_type_id   INTEGER NOT NULL,
    loser_hierarchy_level INTEGER NOT NULL,
    loser_parent_id       INTEGER,
    loser_external_ids_json TEXT NOT NULL DEFAULT '[]',  -- [{source, externalId}]
    loser_child_ids_json    TEXT NOT NULL DEFAULT '[]',  -- [int, int, ...]
    merged_at             DATETIME NOT NULL DEFAULT (datetime('now')),
    merged_by_user_id     INTEGER   -- null = automatic background merge
);
CREATE INDEX idx_merges_winner_id ON media_item_merges(winner_id);
```

### New table: `media_item_duplicate_candidates`

Pre-computed cache of probable duplicate pairs. Populated by a background scan task, not computed live on every page load.

```sql
CREATE TABLE media_item_duplicate_candidates (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    item_a_id   INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
    item_b_id   INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
    detected_at DATETIME NOT NULL DEFAULT (datetime('now')),
    UNIQUE(item_a_id, item_b_id)
);
CREATE INDEX idx_dup_candidates_a ON media_item_duplicate_candidates(item_a_id);
CREATE INDEX idx_dup_candidates_b ON media_item_duplicate_candidates(item_b_id);
```

### New table: `media_item_duplicate_dismissals`

Pairs the user has explicitly marked as "not a duplicate."

```sql
CREATE TABLE media_item_duplicate_dismissals (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    item_a_id   INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
    item_b_id   INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
    dismissed_at DATETIME NOT NULL DEFAULT (datetime('now')),
    UNIQUE(item_a_id, item_b_id)
);
```

`ON DELETE CASCADE` on both FK columns ensures that when either item is deleted (e.g. after a merge), the dismissal record is automatically removed — no stale rows.

### Modified table: `media_items`

Add a pre-computed normalized name column used by the candidate scan query:

```sql
ALTER TABLE media_items ADD COLUMN normalized_name TEXT;
CREATE INDEX idx_media_items_normalized_name ON media_items(normalized_name);
```

`normalized_name` is computed at insert/update time by the service layer:
- lowercase
- strip `.`, `,`, `-`, `'`, `:`
- collapse multiple spaces to one
- trim

Example: `"James S. A. Corey"` → `"james s a corey"`; `"James S.A. Corey"` → `"james sa corey"` — these would still differ, so the normalization should also remove *all* spaces between single letters: `"james s a corey"` → `"james sacorey"` and `"james sa corey"` → `"james sacorey"` — matched. Implemented as a static helper `NormalizeName(string name)` in `MediaItem`-adjacent service code.

---

## Candidate Detection

### Background task: `DuplicateCandidateScanService`

Runs nightly (or on demand via "Rescan" button on the Duplicates page). Replaces the contents of `media_item_duplicate_candidates` each run.

**Algorithm:**
1. Load all `MediaItem` rows with their `normalized_name`, `MediaTypeId`, `HierarchyLevel`, `ParentId`.
2. Group by `(MediaTypeId, HierarchyLevel, ParentId)`.
3. Within each group, find pairs where `normalized_name` is identical.
4. Exclude pairs already in `media_item_duplicate_dismissals`.
5. Exclude pairs where either item is a `loser_original_id` in `media_item_merges` (shouldn't exist — deleted — but defensive).
6. Upsert results into `media_item_duplicate_candidates`.
7. Delete stale candidates (items no longer matching) from `media_item_duplicate_candidates`.

**Performance:** Groups are small (same type + level + parent), so within-group comparison is O(n²) on a small n. The pre-computed `normalized_name` column and index make the grouping query fast. Full scan completes in seconds even on 50k items.

---

## Merge Logic

### Guard checks (before any merge proceeds)

1. Neither item may be a `loser_original_id` in `media_item_merges` (deleted items can't be merged — this should be impossible in normal use but is checked defensively).
2. `winnerId` must be either `id` or `targetId` from the request — prevents merge where winner is an unrelated item.
3. The two items must have the same `MediaTypeId` and `HierarchyLevel`.

### Merge steps

1. **Log written first** (before any destructive changes):
   Snapshot `loser_name`, `loser_media_type_id`, `loser_hierarchy_level`, `loser_parent_id`, all of loser's `media_external_ids` rows, and all of loser's direct child item IDs into `media_item_merges`.

2. **AKA created** (if names differ after normalization):
   Write loser's name to `media_item_aliases` on the winner with `source = "merge"`.

3. **Data consolidated onto winner:**
   - `media_external_ids` — loser's rows moved to winner
   - `media_items` (children) — loser's direct children re-parented to winner; IDs recorded in merge log
   - `user_library` — transferred/merged (better `LibraryStatus` wins; `CompletedAt` and `UserRating` preserved)
   - `interaction_events` — re-pointed to winner
   - `media_list_items` — re-pointed to winner
   - `media_credits` — re-pointed to winner; duplicate `(media_item_id, person_name, role)` tuples deduplicated (loser's credit dropped if winner already has same person+role)
   - `metadata_json` — loser's per-plugin blobs copied into winner for any plugin key winner doesn't already have (lossless; existing winner blobs are not overwritten)

4. **`_resolved` recomputed** on winner via `MetadataResolutionService.ResolveAsync`.

5. **Enrichment reset** on winner: for each plugin associated with a loser external ID source that is now new to the winner (i.e. winner had no existing enrichment row or had `NotFound`/`Exhausted`), reset that enrichment row to `Pending`. This ensures the winner gets a fresh enrichment pass against the new IDs.

6. **`media_item_duplicate_candidates`** — remove any rows referencing either item (pair is no longer a candidate).

7. **Loser deleted** from `media_items`.

---

## Unmerge Logic

### Cascading unmerge problem

If A was merged into B, then B was merged into C:
- Merge log 1: `winner=B, loser=A`
- Merge log 2: `winner=C, loser=B`

When the user unmerges B from C, B is deleted (it's the winner of log 1). The new stub `B'` gets a new ID. We must update merge log 1's `winner_id` from `B` to `B'.Id` so the A→B merge remains valid and the user can later unmerge A from B'.

### Unmerge steps

1. **Load merge log** for `mergeId`.
2. **Create new stub** from log: new `MediaItem` with `loser_name`, `loser_media_type_id`, `loser_hierarchy_level`, `loser_parent_id`. Gets a new ID (`B'.Id`).
3. **External IDs split back**: remove `loser_external_ids_json` entries from winner, add to new stub.
4. **Children re-parented**: items in `loser_child_ids_json` have `ParentId` changed from winner to new stub. Children merged separately (not in this log) stay on winner.
5. **Winner `metadata_json` cleaned**: plugin blobs associated with the loser's external ID sources are removed from winner's `metadata_json`. `_resolved` recomputed on winner.
6. **AKA removed**: delete the `media_item_aliases` row that was created during this merge.
7. **Cascading log update**: find any `media_item_merges` row where `winner_id = winner.Id` AND the loser's children are now under the new stub... actually, more precisely: find rows where `winner_id` equals the old `loser_original_id`'s former `winner` — i.e. find other merge logs whose `winner_id` was the same item that provided the loser's children. 

   More precisely: find all `media_item_merges` rows where `winner_id = {the ID that was the winner of THIS merge record's loser}`. Since the loser no longer exists, we search for merge records that reference `winner_id` matching the `loser_original_id` (the old ID that used to exist). These rows need `winner_id` updated to `B'.Id`.

   Simpler implementation: after creating `B'`, query `media_item_merges WHERE winner_id = (any ID that matches the historical chain)`. Since items are deleted after merge, the practical check is: find all merge logs where `winner_id` is in the set of IDs that were re-parented children of the current merge. This is a single query: `UPDATE media_item_merges SET winner_id = {B'.Id} WHERE loser_original_id IN (SELECT id FROM ... )` — actually the cleanest approach is: **after creating B', scan `media_item_merges` for any row where `winner_id` was the `loser_original_id` of this unmerge and update to `B'.Id`**. This covers the chain correctly.

8. **Enrichment seeded** on new stub: `Pending` enrichment rows for all plugins supporting this media type.
9. **Merge log entry deleted**.
10. **`_resolved` recomputed** on winner.

---

## `normalized_name` maintenance

`normalized_name` is computed and stored whenever a `MediaItem` is created or its `Name` is updated. Set in:
- `MediaItem` creation paths in `FileScanService`, `SyncOrchestrationService`, `ImportService`
- `MergeAndDeleteAsync` (when winner name is potentially updated by `_resolved`)
- `MetadataResolutionService.ResolveAsync` (when `Name` is promoted from `_resolved.title`)

A startup backfill pass populates `normalized_name` for all existing rows (like the existing `BackfillFolderPathsAsync` pattern in `Program.cs`).

---

## API Endpoints

All admin-only unless noted.

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/v1/duplicates` | Paginated list of candidate pairs. Query params: `mediaType`, `page`, `pageSize` |
| `POST` | `/api/v1/duplicates/dismiss` | Body: `{ itemAId, itemBId }`. Mark pair as not a duplicate |
| `POST` | `/api/v1/duplicates/scan` | Trigger an immediate candidate scan (background) |
| `POST` | `/api/v1/media/{id}/merge` | Body: `{ targetId, winnerId }`. Merge two items |
| `GET` | `/api/v1/media/{id}/merges` | List merge history for an item (for unmerge UI) |
| `DELETE` | `/api/v1/media/{id}/merges/{mergeId}` | Unmerge |

`GET /api/v1/media/{id}` response gains two new fields:
- `aliases: string[]` — from `media_item_aliases`
- `mergeHistory: MergeHistoryDto[]` — from `media_item_merges` where `winner_id = id`

---

## Global Search

The existing search query (EF `Like` on `Name` and `MetadataJson`) is extended to also match against `media_item_aliases.alias`. The join is:

```sql
LEFT JOIN media_item_aliases ON media_item_aliases.media_item_id = media_items.id
WHERE media_items.name LIKE '%query%'
   OR media_item_aliases.alias LIKE '%query%'
   OR media_items.metadata_json LIKE '%query%'
```

The `idx_aliases_alias` index makes this fast. Results are deduplicated (a single item with two matching aliases appears once).

---

## UI

### Settings → Duplicates page (`/settings/duplicates`)

- Grouped by media type, paginated
- Each row: two item cards side-by-side (poster, name, hierarchy level, enrichment status)
- Per-row actions: **Merge** (opens comparison modal) and **Dismiss** (hides pair permanently)
- Media type filter dropdown
- **Rescan** button triggers `POST /api/v1/duplicates/scan`
- Empty state: "No duplicate candidates found"

### Side-by-side merge modal

- Shows both items: poster, name, existing aliases, media type, enrichment status, key metadata fields from `resolvedMetadata`
- Radio/highlight to select the winner
- Preview: "Winner's canonical name will be: [name]. '[other name]' will be saved as an AKA."
- **Confirm Merge** (disabled until winner selected)

### Media detail page additions

- **"Merge with…"** in the item action menu → search modal → compare modal
- **Merge History** section (collapsed, at bottom): lists merges involving this item, each with item name, merged date, and **Unmerge** button
- `aliases` displayed below the item title as "Also known as: …" (subtle, secondary text)

---

## Known Gotchas Addressed

| # | Issue | Resolution |
|---|---|---|
| 1 | Merge cycles (merging deleted items) | Guard check: reject merge if either item is a `loser_original_id` in merge log |
| 2 | Cascading unmerge chain breaks | After unmerge creates `B'`, update all merge logs with `winner_id = old-B-id` to `winner_id = B'.Id` |
| 3 | Children merged separately | Only re-parent children explicitly listed in `loser_child_ids_json`; others stay put |
| 4 | Winner needs re-enrichment for new external IDs | After merge, reset enrichment rows to `Pending` for plugins newly introduced by loser's IDs |
| 5 | Stale dismissals after item deletion | `ON DELETE CASCADE` on both FK columns of `media_item_duplicate_dismissals` |
| 6 | Candidate scan performance on large library | Pre-computed `normalized_name` column + indexed; scan runs as background task, results cached in `media_item_duplicate_candidates`; page reads cache |
| 7 | Alias search performance | `idx_aliases_alias` index on `media_item_aliases.alias` |
| 8 | `media_credits` orphaned on merge | `media_credits` added to merge consolidation; deduplication by `(person_name, role)` |

---

## Migration

Sequential SQL migration file (following existing `migrations/` pattern):

```sql
-- Up
ALTER TABLE media_items ADD COLUMN normalized_name TEXT;
CREATE INDEX idx_media_items_normalized_name ON media_items(normalized_name);

CREATE TABLE media_item_aliases ( ... );
CREATE TABLE media_item_merges ( ... );
CREATE TABLE media_item_duplicate_candidates ( ... );
CREATE TABLE media_item_duplicate_dismissals ( ... );

-- Down
DROP TABLE media_item_duplicate_dismissals;
DROP TABLE media_item_duplicate_candidates;
DROP TABLE media_item_merges;
DROP TABLE media_item_aliases;
DROP INDEX idx_media_items_normalized_name;
-- (SQLite doesn't support DROP COLUMN — down migration recreates the table without normalized_name)
```

Startup backfill: `BackfillNormalizedNamesAsync` runs once at startup (like existing `BackfillFolderPathsAsync`) to populate `normalized_name` for all existing rows.
