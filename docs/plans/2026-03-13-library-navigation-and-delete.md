# Library Navigation & Delete — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fold-scoped prev/next navigation, hierarchical "Up" button with library scroll restoration, single-item delete on the detail page, and multi-select batch delete on the library page.

**Architecture:** All changes are frontend-only (React + TypeScript). No backend work required — `deleteMedia(id)` API already exists. Navigation state is passed via React Router's `state` prop; scroll restoration uses URL hash + a `useEffect` in `LibraryPage`. Select mode is local component state.

**Tech Stack:** React 18, TypeScript, React Router v6, TanStack Query, CSS Modules

> **Note:** No frontend test framework is configured. Verification is done visually via the dev preview server after each task. Run `npm run type-check` and `npm run lint` after each task to catch type errors.

---

## Task 1: Fold-scoped Prev/Next

**Files:**
- Modify: `src/Chronicle.Web/src/pages/library/LibraryPage.tsx`

**Context:**
Currently `LibraryPage` computes one `listNavState` from all sorted entries. This means clicking into a movie card from a 6-item fold makes Prev/Next navigate all 50+ movies. The fix moves nav state inside the section loop so it only spans `visible` — the already-sliced subset.

**Step 1: Remove the top-level `listNavState` useMemo**

In `LibraryPage.tsx`, delete these lines (around line 225–228):

```ts
const listNavState = useMemo(() => ({
  listIds: sorted.map(e => e.mediaItem.id),
  listLabel: prefs.statusFilter ? `Library – ${STATUS_LABELS[prefs.statusFilter]}` : 'Library',
}), [sorted, prefs.statusFilter])
```

**Step 2: Add per-section nav state inside the section render loop**

Inside the `mediaTypeNames.map(typeName => { ... })` block, right after `const hasMore = ...`, add:

```ts
const sectionNavState = {
  listIds: visible.map(e => e.mediaItem.id),
  listLabel: typeName,
}
```

**Step 3: Update both Link elements in each card to use `sectionNavState`**

There are two Links per card using `listNavState`. Find and replace both:

```tsx
// Line ~388 — poster link
<Link to={`/media/${entry.mediaItem.id}`} state={sectionNavState} className={styles.posterLink}>

// Line ~411 — name link
<Link to={`/media/${entry.mediaItem.id}`} state={sectionNavState} className={styles.nameLink}>
```

**Step 4: Run type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

Expected: no errors.

**Step 5: Verify in preview**

- Navigate to Library
- Click any item in a section with > 6 items visible
- Confirm the Prev/Next label shows the section name (e.g. `Movies · 1 / 6`), not `Library · 1 / 124`
- Confirm Prev/Next stay within the visible fold

**Step 6: Commit**

```bash
git add src/Chronicle.Web/src/pages/library/LibraryPage.tsx
git commit -m "feat(library): scope prev/next navigation to visible fold"
```

---

## Task 2: Hierarchical "Up" Button

**Files:**
- Modify: `src/Chronicle.Web/src/pages/library/LibraryPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/library/LibraryPage.module.css`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css`

### Part A: Add card IDs to LibraryPage

**Step 1: Add `id` attribute to each card div**

In `LibraryPage.tsx`, find the card wrapper `<div key={entry.id} className={styles.card}>` (around line 387) and add the id:

```tsx
<div key={entry.id} id={`media-${entry.mediaItem.id}`} className={styles.card}>
```

### Part B: Hash scroll effect in LibraryPage

**Step 2: Add `useLocation` import**

At the top of `LibraryPage.tsx`, update the react-router-dom import:

```ts
import { Link, useLocation } from 'react-router-dom'
```

**Step 3: Add `useEffect` for hash scroll**

After the `const pageSize = ...` line, add:

```ts
const location = useLocation()

useEffect(() => {
  if (isLoading) return
  const hash = location.hash  // e.g. "#media-42"
  if (!hash.startsWith('#media-')) return
  const targetId = parseInt(hash.slice('#media-'.length), 10)
  if (isNaN(targetId)) return

  // Find which section and entry this belongs to
  let targetTypeName: string | undefined
  for (const [typeName, entries] of grouped) {
    if (entries.some(e => e.mediaItem.id === targetId)) {
      targetTypeName = typeName
      break
    }
  }
  if (!targetTypeName) return

  // Un-collapse the section if collapsed
  setCollapsedSections(prev => {
    if (!prev[targetTypeName!]) return prev
    const next = { ...prev, [targetTypeName!]: false }
    localStorage.setItem('chronicle.library.collapsed', JSON.stringify(next))
    return next
  })

  // Expand section if item is beyond current page size
  const typeEntries = grouped.get(targetTypeName)!
  const itemIndex = typeEntries.findIndex(e => e.mediaItem.id === targetId)
  if (pageSize !== Infinity && itemIndex >= pageSize) {
    setExpanded(prev => ({ ...prev, [targetTypeName!]: true }))
  }

  // Scroll after a brief delay to allow render
  setTimeout(() => {
    document.getElementById(`media-${targetId}`)?.scrollIntoView({
      behavior: 'smooth',
      block: 'center',
    })
  }, 100)
}, [isLoading, location.hash, grouped, pageSize])
```

### Part C: Up button on MediaDetailPage

**Step 4: Add the Up button to `MediaDetailPage.tsx`**

In the `topNav` div (around line 98), add the Up button after the Back button, before the `{listIds.length > 0 && ...}` block:

```tsx
<div className={styles.topNav}>
  <button className={styles.backBtn} onClick={() => navigate(-1)}>← Back</button>

  {/* Hierarchical Up */}
  {item && (
    item.parentId != null
      ? <Link to={`/media/${item.parentId}`} className={styles.upBtn}>↑ Up</Link>
      : <Link to={`/library#media-${item.id}`} className={styles.upBtn}>↑ Library</Link>
  )}

  {listIds.length > 0 && (
    // ... existing Prev/Next block unchanged
  )}
</div>
```

> Note: `item` is not available during the loading/error states. The Up button is inside the main render, so this is fine — it only renders when `item` is defined (below the `if (isLoading)` / `if (error)` guards).

Actually, `item` is defined in the main return. Move the Up button inside the main `return` block's `topNav`, replacing the conditional guard with the null check shown above.

**Step 5: Add `.upBtn` CSS to `MediaDetailPage.module.css`**

```css
.upBtn {
  font-size: 13px;
  color: var(--text-secondary);
  text-decoration: none;
  background: none;
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 3px 10px;
  transition: color 0.15s, border-color 0.15s;
  white-space: nowrap;
}

.upBtn:hover { color: var(--text-primary); border-color: var(--accent); }
```

**Step 6: Run type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

Expected: no errors.

**Step 7: Verify in preview**

- Navigate to a root-level media item (e.g. a movie) — confirm `↑ Library` button appears
- Click `↑ Library` — confirm library page loads and scrolls to that card
- If the app has a TV show with seasons/episodes, navigate to an episode — confirm `↑ Up` appears and links to the season

**Step 8: Commit**

```bash
git add src/Chronicle.Web/src/pages/library/LibraryPage.tsx \
        src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx \
        src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css
git commit -m "feat(nav): add hierarchical up button with library scroll restoration"
```

---

## Task 3: Delete Single Item (Media Detail Page)

**Files:**
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css`

**Step 1: Add `deleteMedia` import to `MediaDetailPage.tsx`**

Update the media API import line:

```ts
import { getMedia, getMediaChildren, refreshMedia, reidentifyMedia, deleteMedia } from '@/api/media'
```

**Step 2: Add delete state and mutation**

After the `reidentifyMut` block (around line 77), add:

```ts
const [deleteConfirm, setDeleteConfirm] = useState(false)

const deleteMut = useMutation({
  mutationFn: () => deleteMedia(mediaId),
  onSuccess: () => {
    qc.invalidateQueries({ queryKey: ['library'] })
    navigate('/library')
  },
})
```

**Step 3: Add delete button + confirmation strip to the hero section**

In the `<div className={styles.meta}>` block, right after the `<h1 className={styles.title}>` line, add:

```tsx
<div className={styles.deleteArea}>
  {!deleteConfirm ? (
    <button className={styles.deleteBtn} onClick={() => setDeleteConfirm(true)}>
      Delete
    </button>
  ) : (
    <div className={styles.deleteConfirmStrip}>
      <span className={styles.deleteConfirmText}>
        Delete <strong>{item.name}</strong>? This cannot be undone.
      </span>
      <button className={styles.deleteConfirmCancel} onClick={() => setDeleteConfirm(false)}>
        Cancel
      </button>
      <button
        className={styles.deleteConfirmOk}
        onClick={() => deleteMut.mutate()}
        disabled={deleteMut.isPending}
      >
        {deleteMut.isPending ? 'Deleting…' : 'Delete'}
      </button>
    </div>
  )}
</div>
```

**Step 4: Add CSS to `MediaDetailPage.module.css`**

```css
/* ── Delete (single item) ── */

.deleteArea { margin-bottom: 12px; }

.deleteBtn {
  font-size: 12px;
  padding: 3px 10px;
  border-radius: 4px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  transition: color 0.15s, border-color 0.15s;
}

.deleteBtn:hover {
  color: var(--status-danger-fg, #e05555);
  border-color: var(--status-danger-fg, #e05555);
}

.deleteConfirmStrip {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  padding: 8px 12px;
  border-radius: 4px;
  background: rgba(224, 85, 85, 0.08);
  border: 1px solid rgba(224, 85, 85, 0.35);
}

.deleteConfirmText { font-size: 13px; flex: 1; }

.deleteConfirmCancel {
  font-size: 12px;
  padding: 3px 10px;
  border-radius: 4px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  transition: color 0.15s;
}

.deleteConfirmCancel:hover { color: var(--text-primary); }

.deleteConfirmOk {
  font-size: 12px;
  padding: 3px 10px;
  border-radius: 4px;
  border: none;
  background: var(--status-danger-fg, #e05555);
  color: #fff;
  cursor: pointer;
  transition: opacity 0.15s;
}

.deleteConfirmOk:hover:not(:disabled) { opacity: 0.85; }
.deleteConfirmOk:disabled { opacity: 0.5; cursor: not-allowed; }
```

**Step 5: Run type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

Expected: no errors.

**Step 6: Verify in preview**

- Navigate to a media detail page
- Confirm a small "Delete" button appears near the title
- Click Delete — confirm the red confirmation strip appears with Cancel and Delete buttons
- Click Cancel — confirm strip dismisses
- Click Delete again, then confirm Delete — confirm navigation to `/library`

**Step 7: Commit**

```bash
git add src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx \
        src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css
git commit -m "feat(media): add single-item delete with inline confirmation"
```

---

## Task 4: Multi-Select Batch Delete (Library Page)

**Files:**
- Modify: `src/Chronicle.Web/src/pages/library/LibraryPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/library/LibraryPage.module.css`

**Step 1: Add `deleteMedia` import to `LibraryPage.tsx`**

```ts
import { getLibrary, updateLibraryEntry, removeFromLibrary } from '@/api/library'
import { deleteMedia } from '@/api/media'
```

**Step 2: Add select mode state**

After the existing `useState` declarations (around line 141), add:

```ts
const [selectMode, setSelectMode] = useState(false)
const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set())
const [deleteConfirm, setDeleteConfirm] = useState(false)
const [isDeleting, setIsDeleting] = useState(false)
```

**Step 3: Add helper functions**

After `function toggleSection(...)`, add:

```ts
function enterSelectMode() {
  setSelectMode(true)
  setSelectedIds(new Set())
  setDeleteConfirm(false)
}

function exitSelectMode() {
  setSelectMode(false)
  setSelectedIds(new Set())
  setDeleteConfirm(false)
}

function toggleSelected(mediaId: number) {
  setSelectedIds(prev => {
    const next = new Set(prev)
    if (next.has(mediaId)) next.delete(mediaId)
    else next.add(mediaId)
    return next
  })
}

function selectAll() {
  setSelectedIds(new Set(sorted.map(e => e.mediaItem.id)))
}

async function confirmDelete() {
  setIsDeleting(true)
  try {
    for (const id of selectedIds) {
      await deleteMedia(id)
    }
    qc.invalidateQueries({ queryKey: ['library'] })
    qc.invalidateQueries({ queryKey: ['media'] })
  } finally {
    setIsDeleting(false)
    exitSelectMode()
  }
}
```

**Step 4: Add Select / Delete buttons to the controls toolbar**

In the `controlsTop` div, inside `controlsActions`, add before the preset select:

```tsx
{!selectMode ? (
  <button className={styles.actionBtn} onClick={enterSelectMode}>Select</button>
) : (
  <>
    <button
      className={styles.actionBtn}
      onClick={selectAll}
    >
      Select All
    </button>
    <button
      className={styles.deleteModeBtn}
      disabled={selectedIds.size === 0 || isDeleting}
      onClick={() => setDeleteConfirm(true)}
    >
      Delete ({selectedIds.size})
    </button>
    <button className={styles.cancelBtn} onClick={exitSelectMode}>✕ Cancel</button>
  </>
)}
```

**Step 5: Add delete confirmation modal**

Immediately after the `<div className={styles.controls}>` closing tag and before the content, add:

```tsx
{deleteConfirm && (
  <div className={styles.deleteModal}>
    <div className={styles.deleteModalBox}>
      <p className={styles.deleteModalText}>
        Delete <strong>{selectedIds.size}</strong> item{selectedIds.size !== 1 ? 's' : ''}?
        This cannot be undone.
      </p>
      <div className={styles.deleteModalActions}>
        <button
          className={styles.cancelBtn}
          onClick={() => setDeleteConfirm(false)}
          disabled={isDeleting}
        >
          Cancel
        </button>
        <button
          className={styles.deleteConfirmOk}
          onClick={confirmDelete}
          disabled={isDeleting}
        >
          {isDeleting ? 'Deleting…' : 'Delete'}
        </button>
      </div>
    </div>
  </div>
)}
```

**Step 6: Update card render to support select mode**

Replace the card rendering block. In select mode, cards should not navigate — clicking them toggles selection. Find the card `<div>` and wrap/modify as follows:

```tsx
<div
  key={entry.id}
  id={`media-${entry.mediaItem.id}`}
  className={`${styles.card} ${selectMode && selectedIds.has(entry.mediaItem.id) ? styles.cardSelected : ''}`}
  onClick={selectMode ? () => toggleSelected(entry.mediaItem.id) : undefined}
  style={selectMode ? { cursor: 'pointer' } : undefined}
>
  {selectMode ? (
    // In select mode: show poster as non-navigable, with selection overlay
    <div className={styles.posterLink}>
      <div className={styles.poster}>
        {entry.mediaItem.posterUrl ? (
          <img src={entry.mediaItem.posterUrl} alt={entry.mediaItem.name}
            onError={e => {
              const img = e.currentTarget
              img.style.display = 'none'
              const ph = img.nextElementSibling as HTMLElement | null
              if (ph) ph.style.display = 'flex'
            }}
          />
        ) : null}
        <div className={styles.posterPlaceholder} style={{ display: entry.mediaItem.posterUrl ? 'none' : 'flex' }}>
          {entry.mediaItem.name.charAt(0)}
        </div>
      </div>
      {selectedIds.has(entry.mediaItem.id) && (
        <div className={styles.selectOverlay}>✓</div>
      )}
    </div>
  ) : (
    // Normal mode: navigable links (existing markup)
    <Link to={`/media/${entry.mediaItem.id}`} state={sectionNavState} className={styles.posterLink}>
      <div className={styles.poster}>
        {entry.mediaItem.posterUrl ? (
          <img src={entry.mediaItem.posterUrl} alt={entry.mediaItem.name}
            onError={e => {
              const img = e.currentTarget
              img.style.display = 'none'
              const ph = img.nextElementSibling as HTMLElement | null
              if (ph) ph.style.display = 'flex'
            }}
          />
        ) : null}
        <div className={styles.posterPlaceholder} style={{ display: entry.mediaItem.posterUrl ? 'none' : 'flex' }}>
          {entry.mediaItem.name.charAt(0)}
        </div>
      </div>
    </Link>
  )}
  <div className={styles.info}>
    {selectMode ? (
      <div className={styles.name}>{entry.mediaItem.name}</div>
    ) : (
      <Link to={`/media/${entry.mediaItem.id}`} state={sectionNavState} className={styles.nameLink}>
        <div className={styles.name}>{entry.mediaItem.name}</div>
      </Link>
    )}
    <div className={styles.metaRow}>
      {entry.mediaItem.year && <span className={styles.year}>{entry.mediaItem.year}</span>}
      {entry.mediaItem.tmdbMeta?.rating != null && (
        <span className={styles.rating}>★ {entry.mediaItem.tmdbMeta.rating.toFixed(1)}</span>
      )}
    </div>
    {!selectMode && (
      <>
        <select className={styles.statusSelect} value={entry.status}
          onChange={e => updateMut.mutate({ id: entry.id, status: e.target.value as LibraryStatus })}>
          {STATUS_OPTIONS.map(s => (
            <option key={s} value={s}>{STATUS_LABELS[s]}</option>
          ))}
        </select>
        <button className={styles.removeBtn}
          onClick={() => { if (confirm('Remove from library?')) removeMut.mutate(entry.id) }}>
          Remove
        </button>
      </>
    )}
  </div>
</div>
```

**Step 7: Add CSS to `LibraryPage.module.css`**

```css
/* ── Select mode ── */

.deleteModeBtn {
  padding: 4px 12px;
  border-radius: 99px;
  border: 1px solid var(--status-danger-fg, #e05555);
  background: transparent;
  color: var(--status-danger-fg, #e05555);
  font-size: 12px;
  transition: all 0.15s;
}

.deleteModeBtn:hover:not(:disabled) {
  background: var(--status-danger-fg, #e05555);
  color: white;
}

.deleteModeBtn:disabled { opacity: 0.45; cursor: not-allowed; }

.cardSelected {
  border-color: var(--status-danger-fg, #e05555) !important;
  background: rgba(224, 85, 85, 0.08);
}

.selectOverlay {
  position: absolute;
  inset: 0;
  background: rgba(224, 85, 85, 0.25);
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 32px;
  color: #fff;
  font-weight: 700;
}

/* ── Delete confirmation modal ── */

.deleteModal {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 100;
}

.deleteModalBox {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  padding: 24px 28px;
  max-width: 400px;
  width: 90%;
}

.deleteModalText {
  font-size: 14px;
  margin-bottom: 18px;
  line-height: 1.5;
}

.deleteModalActions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.deleteConfirmOk {
  padding: 6px 16px;
  border-radius: 99px;
  border: none;
  background: var(--status-danger-fg, #e05555);
  color: #fff;
  font-size: 12px;
  cursor: pointer;
  transition: opacity 0.15s;
}

.deleteConfirmOk:hover:not(:disabled) { opacity: 0.85; }
.deleteConfirmOk:disabled { opacity: 0.5; cursor: not-allowed; }
```

**Note:** The `.posterLink` div in select mode needs `position: relative` for the overlay to work. The existing `.posterLink` CSS only sets `display: block`. Update it:

```css
.posterLink {
  display: block;
  text-decoration: none;
  position: relative;
}
```

This is already set to `position: relative` in the existing CSS — no change needed.

**Step 8: Run type-check and lint**

```bash
cd src/Chronicle.Web && npm run type-check && npm run lint
```

Expected: no errors.

**Step 9: Verify in preview**

- On Library page: click "Select" button — confirm toolbar changes to show Select All / Delete (0) / ✕ Cancel
- Click some cards — confirm they highlight red with ✓ overlay
- Confirm clicking a highlighted card deselects it
- Click "Select All" — confirm all cards selected
- Click "Delete (N)" — confirm modal appears
- Click Cancel — confirm modal closes, cards still selected
- Click Delete (N) again → Delete in modal — confirm items are removed from library
- Click ✕ Cancel in toolbar — confirm select mode exits without deletion

**Step 10: Commit**

```bash
git add src/Chronicle.Web/src/pages/library/LibraryPage.tsx \
        src/Chronicle.Web/src/pages/library/LibraryPage.module.css
git commit -m "feat(library): multi-select batch delete"
```
