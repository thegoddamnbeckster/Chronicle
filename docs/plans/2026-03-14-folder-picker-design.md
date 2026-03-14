# Folder Picker — Design Document

**Date:** 2026-03-14
**Status:** Approved
**Scope:** Reusable folder-browser modal + `PathInput` wrapper component; initial consumer is the scan page

---

## Problem

Every path input in Chronicle requires users to type directory paths by hand. This is error-prone and inconvenient, especially for UNC paths (`\\NAS\Media`) and deeply nested folders on dedicated media drives.

---

## Decisions

| Question | Decision |
|---|---|
| Picker style | In-browser folder tree modal (Sonarr-style) — no native OS dialog |
| Starting point | Drive roots (`C:\`, `D:\`, etc.) — no home-folder default |
| Text input | Retained and editable; users can paste paths or UNC paths at any time |
| Scope | Reusable component — works everywhere in the app, not scan-page-only |

---

## Architecture

### Backend: `GET /api/v1/filesystem`

New endpoint on `FileScanController` (or a dedicated `FileSystemController`).

**Query parameters:**
- `path` (optional string) — directory to list. Empty/omitted = return drive roots.

**Response:**
```json
{
  "path": "D:\\Movies",
  "parent": "D:\\",
  "directories": [
    { "name": "Action", "path": "D:\\Movies\\Action" },
    { "name": "Drama",  "path": "D:\\Movies\\Drama" }
  ]
}
```

- When `path` is empty: `parent` is `null`, `directories` are the logical drive roots (`C:\`, `D:\`, …)
- When `path` is a valid directory: `parent` is the parent path, `directories` are immediate subdirectories only (no files)
- When `path` is invalid or inaccessible: `400` with a descriptive error message

**Security:** Requires authentication (`[Authorize]`). Only directories visible to the process account are returned; access-denied entries are silently skipped rather than erroring.

---

### Frontend: Two components

#### `FolderPickerModal`

**File:** `src/Chronicle.Web/src/components/FolderPickerModal.tsx`
**CSS module:** `FolderPickerModal.module.css`

**Props:**
```ts
interface FolderPickerModalProps {
  initialPath?: string   // pre-navigate to this path when opened
  onSelect: (path: string) => void
  onClose: () => void
}
```

**Layout (top to bottom):**
1. **Header** — "Browse for Folder" + ✕ close button
2. **Path bar** — editable `<input>` showing the currently browsed path. Pressing Enter navigates to the typed/pasted path. Supports UNC paths (`\\server\share`).
3. **Directory list** — scrollable list of folder rows. Each row shows a folder icon + name and is clickable to navigate into it. A `.. (Up)` row at the top when not at drive roots.
4. **Footer** — selected path in muted text (updates as user navigates) + **Select** button + **Cancel** button

**Behaviour:**
- Opens at drive roots if `initialPath` is empty/undefined; otherwise navigates to `initialPath` immediately
- Clicking a folder navigates into it (re-fetches listing)
- The current folder is the selection — clicking **Select** confirms it
- Pasting into the path bar and pressing Enter: navigates to that path; shows inline error if invalid
- Escape key closes without selecting
- Loading spinner shown while fetching
- Error message shown inline if fetch fails (e.g. access denied)

#### `PathInput`

**File:** `src/Chronicle.Web/src/components/PathInput.tsx`

Drop-in replacement for any `<input type="text">` that accepts a directory path.

**Props:**
```ts
interface PathInputProps {
  value: string
  onChange: (value: string) => void
  placeholder?: string
  id?: string
  className?: string
}
```

**Renders:** full-width text input + `📁 Browse` button side by side. Clicking Browse opens `FolderPickerModal` with `initialPath={value}`. On select, calls `onChange(selectedPath)`.

---

## API type (frontend)

```ts
// src/Chronicle.Web/src/api/filesystem.ts
export interface FilesystemEntry {
  name: string
  path: string
}

export interface FilesystemListing {
  path: string
  parent: string | null
  directories: FilesystemEntry[]
}

export async function listDirectory(path: string): Promise<FilesystemListing>
```

---

## Consumer: Scan page

The scan page's "Directory path" `<input>` is replaced with `<PathInput>`. No other scan page changes required.

```tsx
// Before
<input type="text" value={path} onChange={(e) => setPath(e.target.value)} />

// After
<PathInput value={path} onChange={setPath} placeholder="C:\Movies" id="scan-path" />
```

---

## Error handling

| Scenario | Behaviour |
|---|---|
| Path not found | Inline error in path bar: "Path not found" |
| Access denied | Inline error: "Access denied" |
| Network error | Inline error: "Could not reach server" |
| Drive roots empty | Show message: "No drives found" |

---

## Testing

- Integration test: `GET /api/v1/filesystem` with no path returns drives
- Integration test: `GET /api/v1/filesystem?path=C:\` returns directories
- Integration test: `GET /api/v1/filesystem?path=nonexistent` returns 400
- Integration test: unauthenticated request returns 401
- Frontend: `PathInput` renders text input + Browse button; clicking Browse opens modal
- Frontend: `FolderPickerModal` shows drive roots on open; navigates on folder click; calls `onSelect` on confirm

---

## Platform notes

### Linux / Docker
`DriveInfo.GetDrives()` returns mount points on Linux rather than drive letters. On a typical Docker container this means a single root `/`; on a host with multiple mounts there will be several entries. The backend returns whatever the OS provides — no special casing required. Path separators are forward slashes on Linux; the frontend must not hard-code `\` as a separator. The modal will function identically; the "drive roots" list just shows mount points instead of `C:\`, `D:\`, etc.

### Mobile / small viewports
The modal must be usable on a smartphone:
- **Width:** `min(540px, 95vw)` so it fills the screen on narrow viewports
- **Touch targets:** folder rows must be at least 44px tall
- **Path bar:** full-width, large enough for a virtual keyboard; consider `font-size: 16px` to prevent iOS auto-zoom
- **Footer buttons:** full-width stacked on viewports narrower than ~380px

These are CSS-only concerns and do not affect component props or API shape.

---

## Out of scope

- Files (only directories shown)
- Favourites / recently used paths (can be added later)
- Cross-platform path normalisation (server returns whatever the OS provides)
- Persistent scan folders (separate backlog item)
