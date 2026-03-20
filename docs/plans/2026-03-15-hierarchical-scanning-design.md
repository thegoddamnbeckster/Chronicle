# Hierarchical File Scanning & Smart Import — Design

**Date:** 2026-03-15
**Status:** Approved
**Scope:** File scanner grouping, import preview UI, library drill-down, Up button navigation, clean slate migration, nuclear library reset

---

## Problem

The file scanner currently creates every music file as a flat root-level `MediaItem` (`HierarchyLevel=0`, `ParentId=null`). A Music library of 7,594 tracks appears as 7,594 individual cards in the Library page instead of a handful of Artists. The `rootOnly` filter in the library query is already correct — the data structure is wrong.

---

## Goals

1. Scanner groups files into a proper hierarchy (Artist→Album→Track, Show→Season→Episode) or flat groups (Audiobook parts) before import
2. Multiple signal sources (folder structure, embedded tags, NFO files) combine into a per-group confidence score
3. Import preview shows only root items with confidence scores; user accepts/rejects at root level
4. Library page shows only root items (unchanged — `rootOnly` filter already handles this)
5. Any media detail page with a parent gets an Up button for upward hierarchy traversal
6. Clean slate migration wipes existing flat items so user can re-scan with new logic
7. Nuclear reset wipes the entire library with a serious confirmation gate

---

## Two Grouping Modes

The scanner chooses a grouping mode per media type based on `MediaType.HierarchyLevels`:

| Mode | HierarchyLevels | Example | Behaviour |
|---|---|---|---|
| **Flat-grouped** | 1 | Audiobook, single Movie | All files in the same folder = one item. Files are stored as parts but are not navigable sub-levels. |
| **Hierarchical** | 2+ | Music (3), TV (3) | Folder depth maps to hierarchy levels. Each level is a distinct, navigable concept. |

---

## Scanner Pipeline (3 stages)

### Stage 1 — Signal Extraction

For every file encountered during a scan, extract all available signals:

**Folder signals** (weight 0.5)
- Relative path from scan root
- Folder depth
- Parent folder name(s)
- Naming patterns: `Season XX`, `SxxExx`, `Disc N`, `Part N`, year in parens

**Tag signals** (weight 0.7)
- Read via **TagLib#** for any audio/video file that supports embedded tags (MP3, FLAC, M4A, MP4, MKV, OGG, OPUS, WMA, AAC, etc.)
- Fields used: `Artist`, `AlbumArtist`, `Album`, `Title`, `TrackNumber`, `DiscNumber`, `Year`, `Genre`
- Image files (`.jpg`, `.png`, `.webp`) and `.nfo` files produce no tag signals — that is expected and fine

**NFO signals** (weight 0.7)
- If a `.nfo` file exists in the same folder (or is the scanned file itself), parse it as XML
- Fields: `<title>`, `<artist>`, `<album>`, `<showtitle>`, `<season>`, `<episode>`, `<year>`, `<plot>`, `<thumb>`
- NFO files are not imported as MediaItems themselves — they are metadata sources only

### Stage 2 — Confidence Scoring

Each signal source casts a weighted vote on:
- What the item is (media type, hierarchy level)
- Which root group it belongs to (group key = artist name, show name, audiobook title, etc.)

Confidence formula:
- Base score = weighted average of agreeing signals
- Agreement bonus: if folder name AND tag AND NFO all agree on the group key, multiply by 1.2 (capped at 1.0)
- Conflict penalty: if tag says `Artist=X` but folder says `Artist=Y`, subtract 0.15

The group's displayed confidence is the mean of its member scores, penalised 0.1 for each internal conflict.

Confidence thresholds:
- **≥ 0.80** — green badge, ready to accept
- **0.50–0.79** — amber badge, review recommended
- **< 0.50** — red badge, manual intervention needed

### Stage 3 — Tree Assembly

Files are assembled into a candidate `ScanGroup` tree:

**Hierarchical types (HierarchyLevels ≥ 2):**
- Depth 0 from scan root → HierarchyLevel 0 (Artist, Show)
- Depth 1 → HierarchyLevel 1 (Album, Season)
- Depth 2+ → HierarchyLevel 2 (Track, Episode)
- Tag signals can override folder-inferred level (e.g. a tag-confirmed Artist name takes priority over a folder name)

**Flat-grouped types (HierarchyLevels = 1):**
- All files under the same immediate folder = one candidate item
- Individual files are stored as part records in `metadata_json` but do not become navigable MediaItem children

Files that cannot be confidently attached to any root group go into an **Ungrouped** bucket, surfaced separately in the import UI for manual review.

---

## New Service: `ScanGroupingService`

Extracted from `FileScanService` to own the tree-building logic. Responsibilities:
- Accept a flat list of `ScannedFile` records (path + file info)
- Run all three pipeline stages
- Return a `ScanGroupResult` containing a list of `ScanGroup` trees and an `Ungrouped` list

`FileScanService` becomes an orchestrator: walk the filesystem, hand files to `ScanGroupingService`, persist the resulting trees.

**TagLib# dependency** added to `Chronicle.Services.csproj`.

---

## Import Preview UI Changes

The scan preview page currently shows a flat file list. After this change:

- The results panel shows one row per **root item** (Artist, Show, Audiobook)
- Each row displays: poster/folder image, detected name, media type, item count, confidence badge
- An expand chevron reveals the candidate hierarchy (Albums → Tracks, Seasons → Episodes)
- Sub-items in the expanded view are read-only — they show what will be imported but have no individual accept/reject
- Accept/Reject operates at the root level only
- A separate **"Ungrouped files"** section at the bottom lists files that couldn't be grouped, each with a manual-assign option

Confidence badge colours match the thresholds above.

---

## Library Page (no change to query logic)

The `rootOnly` filter (`WHERE ParentId IS NULL`) remains unchanged. Once the scanner creates proper hierarchies, only root items appear in the library automatically. No query changes needed.

---

## Hierarchy Drill-Down & Up Button

### Library → Detail navigation

- Clicking a root card (Artist, Show) navigates to that item's detail page
- The detail page renders its children as a grid/list (Albums for Artist, Seasons for Show)
- Clicking a child card goes one level deeper (Album → Track list, Season → Episode list)
- Terminal-level items (Track, Episode) show playback/scrobble info, no further drill-down

### Up button

- Every detail page for an item with a `ParentId` renders an **Up** button in the page header, left of the title
- Label: `↑ {ParentName}` (e.g. "↑ Black Album", "↑ Breaking Bad")
- Navigates to `/media/{parentId}`
- Root items (no `ParentId`) show no Up button

This gives full two-way traversal of any hierarchy depth.

---

## Clean Slate Migration

A targeted reset for users who have existing flat-scanned data. Available in **Settings → Library**.

Steps:
1. User clicks "Clear Flat Scan Data" (amber button, not the nuclear one)
2. Confirmation modal: lists the media types affected and item counts
3. On confirm:
   - Delete all `UserLibrary` entries for affected items
   - Delete all `MediaItem` records that were created by the file scanner with `HierarchyLevel = 0` AND have no children (i.e. are genuinely flat)
   - Or, if the user chooses "Clear All File Scanner Items": delete all file-scanner-sourced items regardless of hierarchy
4. User re-runs the file scanner to re-import with new grouping logic

Source tracking: `MetadataJson` for file-scanner items will include `"source": "fileScanner"` so they can be identified unambiguously.

---

## Nuclear Library Reset

In **Settings → Library** (Danger Zone section), a **red "Reset Entire Library" button**.

Confirmation flow (two steps):
1. Click button → modal opens with stark plain-language warning listing everything that will be permanently deleted:
   - All MediaItems (N items)
   - All library entries (N)
   - All interaction/scrobble events (N)
   - All scan history
   - All user ratings and notes
2. Modal requires user to type `RESET` into a text field to unlock the confirm button
3. On confirm: truncate all affected tables, redirect to empty Library page

This is distinct from the clean slate migration. It is a full factory reset of all media data.

---

## New NuGet Dependencies

| Package | Project | Purpose |
|---|---|---|
| `TagLibSharp` | `Chronicle.Services` | Read embedded audio/video tags |

---

## Out of Scope

- MusicBrainz / TMDB lookup during scan (metadata enrichment is a separate post-import step)
- Automatic re-scan scheduling (existing background task system handles this)
- Editing the hierarchy after import (future feature)
