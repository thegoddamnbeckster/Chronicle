# Library Navigation & Delete — Design

**Date:** 2026-03-13
**Status:** Approved

---

## Overview

Three related improvements to the library and media detail pages:

1. **Fold-scoped Prev/Next** — navigation in the detail page stays within the visible fold of the library section you came from, not the full sorted list.
2. **Hierarchical Up navigation** — new "↑ Up" button that moves up the media hierarchy (episode → season → show → library) with hash-based scroll restoration in the library.
3. **Delete** — single-item delete on the detail page, and multi-select batch delete on the library page.

---

## Feature 1: Fold-scoped Prev/Next

### Problem

`LibraryPage` currently computes one `listNavState` from `sorted.map(e => e.mediaItem.id)` — all entries across all sections. When a section shows only 6 of 50 movies, clicking into one makes Prev/Next navigate through all 50.

### Solution

Replace the single top-level `listNavState` useMemo with per-section nav state computed inside the section render loop from `visible` (the already-sliced subset):

```ts
const sectionNavState = {
  listIds: visible.map(e => e.mediaItem.id),
  listLabel: typeName,
}
```

Each card link passes `sectionNavState`. `MediaDetailPage` is unchanged — it already reads whatever `listIds` it receives from router state.

**Label format:** `Movies · 3 / 6`

---

## Feature 2: Hierarchical Up Navigation

### Controls on the detail page top bar

| Control | Behavior |
|---|---|
| `← Back` | `navigate(-1)` — browser history, unchanged |
| `‹ Prev` / `Next ›` | Fold-scoped list navigation (Feature 1) |
| `↑ Up` | New — hierarchical parent navigation |

### Up button logic

- `item.parentId != null` → `<Link to="/media/${item.parentId}">↑ Up</Link>`
- `item.parentId == null` → `<Link to="/library#media-${item.id}">↑ Library</Link>`

### Library scroll restoration (hash-based)

Each library card div gets `id="media-{mediaItem.id}"`.

`LibraryPage` adds a `useEffect` that fires when data is loaded and the URL has a `#media-{id}` hash:

1. Extract the target media ID from the hash.
2. If the target's section is collapsed, un-collapse it.
3. If the target is beyond the current page size fold, expand that section.
4. Call `document.getElementById('media-{id}')?.scrollIntoView({ behavior: 'smooth', block: 'center' })`.

---

## Feature 3: Delete

### A) Media detail page — single delete

- Small destructive (red-tinted) `Delete` button placed in the top-right of the hero meta area.
- Clicking shows an inline confirmation strip below the button:
  - Text: `"Delete [Title]? This cannot be undone."`
  - Buttons: `[Cancel]` `[Delete]`
- On confirm: calls `deleteMedia(mediaId)`, then `navigate('/library')`.

### B) Library page — multi-select batch delete

**Entering select mode:**
- New `Select` toggle button in the controls toolbar.
- In select mode, a visible `✕ Cancel` button exits without action.

**In select mode:**
- Cards are not navigable — clicking selects/deselects instead.
- Selected cards show a checkmark overlay and a highlighted border.
- Toolbar shows: `Select All` button + `Delete (N)` button (disabled when N = 0).

**Delete flow:**
- `Delete (N)` click → inline modal/dialog: *"Delete N items? This cannot be undone."* `[Cancel]` `[Delete]`
- On confirm: calls `deleteMedia(id)` for each selected ID, then invalidates queries, exits select mode, clears selection.

**API:** `deleteMedia(id: number): Promise<void>` in `src/Chronicle.Web/src/api/media.ts` already exists.

---

## Files to Change

| File | Change |
|---|---|
| `src/Chronicle.Web/src/pages/library/LibraryPage.tsx` | Per-section nav state, card IDs, hash scroll effect, select mode state and UI |
| `src/Chronicle.Web/src/pages/library/LibraryPage.module.css` | Selected card styles, select mode toolbar styles, delete button |
| `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx` | Up button, delete button + inline confirmation |
| `src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css` | Up button style, delete button style, confirmation strip style |
