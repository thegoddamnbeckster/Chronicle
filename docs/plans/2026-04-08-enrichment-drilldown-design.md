# Enrichment Drill-Down Page — Design

**Date:** 2026-04-08
**Status:** Approved

---

## Overview

When the Background Tasks page shows enrichment counts (Failed: 12, Skipped: 5, etc.), clicking
any count navigates to a drill-down page for that plugin and status. The drill-down shows the
actual media items behind the number, with context-appropriate actions to resolve them.

This is the primary tool for managing enrichment health at scale — knowing enrichment is "stuck"
is only useful if you can see *what* is stuck and act on it.

---

## Route

```
/library/enrichment/:pluginId/:status
```

Examples:
- `/library/enrichment/tmdb/failed`
- `/library/enrichment/chronicle.plugin.musicbrainz/skipped`

`status` is one of: `failed`, `exhausted`, `not_found`, `skipped`, `pending`, `completed`
(lowercase, matching `EnrichmentStatus` enum values).

---

## Entry Point

On the Background Tasks page, each status count in the enrichment box becomes a clickable link:

```
TMDB
  Completed   234    ← link → /library/enrichment/tmdb/completed
  Pending      45    ← link → /library/enrichment/tmdb/pending
  Failed       12    ← link → /library/enrichment/tmdb/failed
  Skipped       5    ← link → /library/enrichment/tmdb/skipped
  Exhausted     3    ← link → /library/enrichment/tmdb/exhausted
  Not Found     7    ← link → /library/enrichment/tmdb/not_found
```

Zero counts are still links (navigating there shows an empty state, not an error).

---

## Page Layout

### Header
- Plugin name + current status label: **"TMDB — Failed Items"**
- Count of items currently shown: "12 items"
- "Reset All to Pending" bulk action button (disabled on Pending and Completed tabs)

### Tab Bar
One tab per enrichment status. Each tab shows its live count:

```
Failed (12)  |  Skipped (5)  |  Exhausted (3)  |  Not Found (7)  |  Pending (45)  |  Completed (234)
```

- Active tab is highlighted
- Switching tabs updates the URL (`:status` segment) and reloads the list
- Counts refresh every 10 seconds automatically
- Counts also refresh immediately after any action is taken on the page

### Item List

Paginated list of media items matching the current plugin + status filter. Each row shows:
- Poster thumbnail (small)
- Item name + hierarchy path (e.g. "Space: Above and Beyond › Season 1 › S01E22 Sugar Dirt")
- Media type badge
- Last attempt date (for Failed/Exhausted)
- Error message snippet (for Failed) — truncated, full text in tooltip
- Action buttons (see below)

### Empty State
If no items match the current filter: "No [status] items for [Plugin Name]."

---

## Per-Item Actions

Actions are context-sensitive — only actions meaningful for the current status are shown.

| Tab | Available Actions |
|---|---|
| **Failed** | Reset to Pending · Skip · Fix Match · → Detail Page |
| **Exhausted** | Reset to Pending · Skip · Fix Match · → Detail Page |
| **Not Found** | Reset to Pending · Skip · Fix Match · → Detail Page |
| **Skipped** | Reset to Pending · Fix Match · → Detail Page |
| **Pending** | Skip · → Detail Page |
| **Completed** | Refresh (re-enrich) · Fix Match · → Detail Page |

**Reset to Pending:** Resets this item's enrichment row to Pending so it will be picked up on
the next enrichment pass. Clears error message and attempt count.

**Skip:** Marks the item's enrichment row as Skipped. It will not be retried automatically.
Only removed from the queue by an explicit Reset to Pending.

**Fix Match:** Opens the Fix Match panel (same as on the media detail page). After a successful
fix match, the item's row refreshes in place or disappears from the current filter as appropriate.

**Refresh (Completed only):** Forces a re-enrichment pass for this item, resetting to Pending
so the plugin fetches fresh data.

**→ Detail Page:** Navigates to the full media detail page for the item.

---

## Bulk Action

**"Reset All to Pending"** button in the page header:
- Resets every item in the current filtered view to Pending
- Only enabled on: Failed, Exhausted, Not Found, Skipped tabs
- Disabled on: Pending (already pending), Completed (use Refresh instead)
- Shows a confirmation prompt before executing (count of items to be reset)
- After completion, the list empties (all items moved out of current filter) and tab counts refresh

---

## Live Updates

Tab counts and the item list must always reflect current state:

- **Polling:** Tab counts re-fetch every 10 seconds (same interval as Background Tasks page)
- **Action-triggered:** After any per-item or bulk action, tab counts refresh immediately and
  the affected row either disappears (status changed, no longer matches filter) or updates in place
- **No full-page reload:** Updates are incremental — only the count and affected rows change,
  the rest of the page stays stable

---

## File Scanner Special Case

The File Scanner does not use the `media_enrichment` table and does not have enrichment statuses.
Its background tasks box on the Background Tasks page links to the Scan page rather than the
drill-down. No drill-down page exists for the File Scanner plugin.

---

## API

### GET /api/v1/enrichment/{pluginId}/items

Returns paginated media items with the given enrichment status for the given plugin.

**Query params:**
- `status` — one of: `Pending`, `Failed`, `Exhausted`, `NotFound`, `Skipped`, `Completed`
- `page` — 1-based page number (default: 1)
- `pageSize` — items per page (default: 25, max: 100)

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "mediaItemId": 243915,
      "name": "S01E22 Sugar Dirt",
      "hierarchyPath": "Space: Above and Beyond › Season 1",
      "mediaType": "tv",
      "posterUrl": "...",
      "status": "Failed",
      "errorMessage": "No candidates scored above threshold",
      "attemptCount": 3,
      "lastAttemptAt": "2026-04-07T03:00:00Z"
    }
  ],
  "pagination": { "page": 1, "pageSize": 25, "totalItems": 12, "totalPages": 1 }
}
```

### GET /api/v1/enrichment/{pluginId}/stats

Returns counts per status for the given plugin. Already partially exists via `GetStatsAsync`;
extend to be callable per-plugin at any time (not just from background tasks aggregation).

**Response:**
```json
{
  "success": true,
  "data": {
    "pluginId": "tmdb",
    "pluginName": "TMDB",
    "counts": {
      "Pending": 45,
      "Completed": 234,
      "Failed": 12,
      "Exhausted": 3,
      "NotFound": 7,
      "Skipped": 5
    }
  }
}
```

### POST /api/v1/enrichment/{pluginId}/items/{mediaItemId}/reset

Resets one item's enrichment row to Pending. Returns the updated enrichment row.

### POST /api/v1/enrichment/{pluginId}/items/{mediaItemId}/skip

Marks one item as Skipped. Returns the updated enrichment row.

### POST /api/v1/enrichment/{pluginId}/items/reset-by-status

Bulk reset all items of a given status to Pending.

**Body:** `{ "status": "Failed" }`

**Response:** `{ "success": true, "data": { "resetCount": 12 } }`

---

## Out of Scope

- Filtering/searching within the drill-down list (can be added later)
- Sorting (default is most recently attempted first for Failed/Exhausted, alphabetical for others)
- Cross-plugin bulk operations (each plugin's drill-down is independent)
