# Design: Hierarchical Import, Library UX & Plugin Settings

**Date:** 2026-03-08
**Status:** Approved
**Repos affected:** Chronicle (this repo), Chronicle.Plugin.FileScanner

---

## Background

After the initial file scan and direct import, the library showed 500 individual TV show
entries — one per episode file rather than one per show. Additionally, the TMDB plugin was
unhealthy because no API key had been configured (no settings UI existed), and the media
detail page was missing its TMDB icon and FileScanner metadata panel for directly-imported
items.

This design covers:

1. FileScanner plugin enhanced metadata extraction (season/episode, embedded tags)
2. Hierarchical import: TV Show → Season → Episode; Artist → Album → Track
3. Clear library endpoint (admin, scoped to user)
4. Library display: root-level items only + collapsible sections with persistent state
5. Plugin settings UI (configure TMDB API key and other plugin settings)
6. Media detail page bug fixes (TMDB icon, FileScanner box, 502 on refresh)
7. Backlog additions (movie collections, dynamic library loading)

---

## 1. FileScanner Plugin — Enhanced Metadata Extraction

> Full design in `Chronicle.Plugin.FileScanner/docs/plans/2026-03-08-enhanced-metadata-extraction.md`

### New `ScannedFile` fields (added to `Chronicle.Plugins/ScannedFile.cs`)

```csharp
// ── TV / Episode hierarchy ──────────────────────────────────────────────────
public string?  ShowTitle       { get; init; }  // "21st Century Renovation"
public int?     SeasonNumber    { get; init; }  // 1
public int?     EpisodeNumber   { get; init; }  // 5
public string?  EpisodeTitle    { get; init; }  // "Episode Title" (when in filename)

// ── Music / Audio tags ──────────────────────────────────────────────────────
public string?  AudioArtist      { get; init; }
public string?  AudioAlbumArtist { get; init; }
public string?  AudioAlbum       { get; init; }
public int?     AudioTrackNumber { get; init; }
public int?     AudioDiscNumber  { get; init; }
public int?     AudioYear        { get; init; }
public string?  AudioGenre       { get; init; }

// ── Container / embedded video tags ────────────────────────────────────────
public string?  ContainerTitle       { get; init; }  // MKV/MP4 title tag
public int?     ContainerYear        { get; init; }
public string?  ContainerDescription { get; init; }

// ── Technical ───────────────────────────────────────────────────────────────
public int?     DurationSeconds { get; init; }
public long?    FileSizeBytes   { get; init; }
```

### FileScanner plugin changes

**New dependency:** `TagLib#` NuGet package — reads embedded tags from MP3, FLAC, OGG,
M4A, MP4, MKV, AVI, and most other formats without requiring external binaries.

**New file: `EmbeddedTagReader.cs`**
- Uses TagLib# to open each media file and extract audio tags
  (ID3v2/ID3v1, Vorbis Comments, MP4 atoms, ASF/WMA tags, Matroska tags)
- Returns a struct with all nullable tag fields
- Catches all TagLib exceptions; returns empty struct on failure (never throws)
- Also reads `Duration` and `FileAbstraction.FileLength` for technical fields

**Updated `FileNameParser.cs`**
- Existing `TvEpisodeCode` regex extended to also capture group values:
  `S(\d{1,2})E(\d{1,2})` → SeasonNumber, EpisodeNumber
  `(\d{1,2})[xX](\d{2})` → SeasonNumber, EpisodeNumber
- `Parse()` returns `ShowTitle` (everything before the SxxExx code, cleaned)
  and `EpisodeTitle` (everything after, if present)
- `ParsedTitle` for TV files is set to `EpisodeTitle ?? ShowTitle`
  (episode-level title, matching existing behaviour for backward compat)

**Updated `FileScannerPlugin.cs`**
- Supported media types gains `"music"` entry
- Audio extensions added: `.mp3`, `.flac`, `.ogg`, `.m4a`, `.aac`, `.wma`, `.opus`
- `ScanDirectoryAsync` pipeline:
  1. Parse filename (season/episode or title/year)
  2. Try NFO sidecar (existing)
  3. **New:** Read embedded tags via `EmbeddedTagReader`
  4. Merge: NFO wins over tags wins over filename heuristics for each field
  5. Attach local poster (existing)

---

## 2. Hierarchical Import in `FileScanService`

### Strategy selection

The media type's `HierarchyLevels` count determines import strategy:

| HierarchyLevels | Strategy |
|----------------|----------|
| 1 | Flat — one MediaItem per file (movies, existing behaviour) |
| 3 | Three-tier — group into root → mid → leaf (TV, music) |
| 2 | Two-tier — group into root → leaf (e.g. podcast series → episode) |

### TV import (3-tier)

Grouping key for the root (Show): `ShowTitle ?? ParsedTitle`

```
For each distinct ShowTitle in the batch:
  1. Find existing MediaItem WHERE name = showTitle AND type = TV AND parent_id IS NULL
     OR create new Show item (HierarchyLevel = 0)
  2. For each distinct SeasonNumber within that show:
       Find or create Season item (HierarchyLevel = 1, parent = show,
         name = "Season {N}", Number = SeasonNumber)
  3. For each episode file in that season:
       Create Episode item (HierarchyLevel = 2, parent = season,
         name = EpisodeTitle ?? ParsedTitle,
         Number = EpisodeNumber)
       Store file path in metadata_json.fileScanner.filePath
  4. Upsert ONE user_library entry for the Show (not seasons or episodes)
```

Season 0 (specials / no season number) → `name = "Specials"`, `Number = 0`.

### Music import (3-tier)

Grouping key for root (Artist): `AudioArtist ?? AudioAlbumArtist ?? ParsedTitle`

```
For each distinct Artist:
  1. Find or create Artist item (HierarchyLevel = 0)
  2. For each distinct Album:
       Find or create Album item (HierarchyLevel = 1, parent = artist)
  3. For each track:
       Create Track item (HierarchyLevel = 2, parent = album,
         Number = AudioTrackNumber,
         name = ParsedTitle)
       Store file path, disc number, genre in metadata_json
  4. Upsert ONE user_library entry for the Artist
```

### Find-or-create semantics

`FindOrCreateParentAsync(name, mediaTypeId, parentId, level)`:
- Queries by (name, mediaTypeId, parentId, HierarchyLevel) — case-insensitive match
- Creates if not found; returns existing if found
- Uses a per-import in-memory dictionary to avoid duplicate DB roundtrips within a batch

### Metadata storage (lossless ingestion)

All new ScannedFile fields are stored in `metadata_json` partitioned by source:

```json
{
  "fileScanner": {
    "filePath": "/media/tv/Show/S01/S01E01.mkv",
    "durationSeconds": 2640,
    "fileSizeBytes": 8589934592,
    "audioArtist": null,
    "audioAlbum": null,
    "containerTitle": "21st Century Renovation - Episode Title",
    "containerYear": 2023
  }
}
```

---

## 3. Clear Library Endpoint

```
DELETE /api/v1/library/all
Authorization: Bearer {jwt}
```

- Deletes all `user_library` entries for the authenticated user
- Cascades to delete all `media_items` that are exclusively owned by this user
  (items with no library entries from other users)
- Returns: `{ "success": true, "data": { "removedItems": 512 } }`
- Admin-only: returns 403 if caller is not admin

---

## 4. Library Display Changes

### Backend: `rootOnly` filter

`GET /api/v1/library?rootOnly=true` adds:

```sql
WHERE media_items.parent_id IS NULL
```

The frontend always passes `rootOnly=true`. Default remains `false` for API
backwards compatibility.

### Frontend: collapsible sections

Each media-type section header gains a chevron toggle button.

**Persistence:** `localStorage` key per section:
```
chronicle.library.collapsed.<mediaTypeName>   → "true" | "false"
```

Initialised on mount from localStorage. Default: expanded (no key set = expanded).
State is per-browser, not synced to server.

**Behaviour:**
- Collapsed: header row visible, count badge visible, card grid hidden
- Expanded: full card grid visible
- Toggle persists immediately on click (no debounce needed)

---

## 5. Plugin Settings UI

**Location:** Existing Plugins page (`/settings/plugins`)

**Trigger:** A "Configure" button appears on each plugin card that has a non-empty
settings schema (i.e. `GET /api/v1/plugins/{id}/settings-schema` returns at least
one field).

**Interaction pattern:** Inline expandable panel below the plugin card (not a modal).

**Form rendering:**
- Fields generated dynamically from the schema's `SettingDefinition` list
- Field type `"secret"` → `<input type="password">` with show/hide toggle
- Field type `"string"` → `<input type="text">`
- Field type `"bool"` → `<input type="checkbox">`
- Required fields marked with `*`

**Save flow:**
1. User enters values and clicks "Save"
2. `PUT /api/v1/plugins/{id}/settings` with `{ settings: { key: value, ... } }`
3. On success: panel shows green "Saved" toast; health badge refreshes automatically
4. On failure: inline error message

**TMDB helper text:** When the plugin ID is `"tmdb"`, show a helper link below the
`api_key` field: *"Get a free API key at [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api)"*

No new backend endpoints required.

---

## 6. Media Detail Page Bug Fixes

### Bug 1 — TMDB 502 on Refresh

**Root cause:** `RefreshMetadataAsync` throws when no provider is configured (no API key),
and the catch-all maps all exceptions to 502.

**Fix in `FileScanService.RefreshMetadataAsync`:**
```csharp
var provider = _registry.GetLoadedPlugins()
    .SelectMany(p => p.MetadataProviders)
    .FirstOrDefault();

if (provider is null)
    throw new InvalidOperationException("NO_PROVIDER_CONFIGURED");
```

**Fix in `MediaController`:** Catch `InvalidOperationException` with message
`"NO_PROVIDER_CONFIGURED"` and return `409 Conflict` with error code
`NO_PROVIDER_CONFIGURED`. All other exceptions remain as 502.

**Frontend:** When refresh returns 409 with `NO_PROVIDER_CONFIGURED`, show:
*"No metadata provider configured. Add an API key in Settings → Plugins."*

### Bug 2 — Missing TMDB icon

**Root cause:** The detail page looks up the plugin icon by matching `pluginId === "tmdb"`
in the installed plugins list. If the list hasn't loaded yet (race), the icon URL is
undefined and the `<img>` silently fails.

**Fix:** Bundle a static `tmdb-logo.svg` asset in the frontend. Use the API icon proxy
as primary source; fall back to the bundled SVG if the proxy URL is unavailable.

### Bug 3 — Missing FileScanner metadata box

**Root cause:** `import-direct` stores the file path at `metadata_json.fileScanner.filePath`
(new partitioned format) but the `FileScannerMetaDto` parser in the frontend still checks
for the legacy flat key `metadata_json.filePath` as a fallback.

**Fix in `MediaDetailPage.tsx`:** Ensure the parser checks both paths:
1. `metadata_json.fileScanner.filePath` (new format — import-direct)
2. `metadata_json.filePath` (legacy flat format — old scan+identify)

---

## 7. Backlog Additions

Added to `BACKLOG.md` under **Planned**:

### Movie Collections

Group movies into named collections (e.g. "Alien Collection", "Avengers Collection").
TMDB returns `belongs_to_collection` on each movie response. Collections have their own
TMDB ID, name, overview, poster, and backdrop.

**Data model:** A `media_groups` record represents each collection. Member movies set
`media_group_id` FK. Collections are browseable from the library and from each
member movie's detail page.

**Implementation touches:** TMDB plugin (emit collection info), MetadataRefreshService
(create/link groups), library UI (collection badge on movie cards).

### Dynamic Library Loading

Replace the single `getLibrary(undefined, 1, 500)` call with paginated loading.
Library sections load section-by-section or via infinite scroll. Count badges appear
immediately (from a lightweight count query); cards fill in progressively.
Prevents the page from freezing on large libraries (1000+ items).

---

## Implementation Sequence

See `docs/plans/2026-03-08-hierarchical-import-and-library-ux-plan.md` for the
full step-by-step implementation plan.

High-level order:

1. Extend `ScannedFile` model (Chronicle.Plugins shared library)
2. Update FileScanner plugin (add TagLib#, EmbeddedTagReader, extended FileNameParser)
3. Cut FileScanner v1.1.0 release
4. `FileScanService` hierarchical import logic
5. `DELETE /api/v1/library/all` endpoint
6. Library API `rootOnly` filter
7. Media detail page bug fixes (502, icon, FileScanner box)
8. Plugin settings UI (frontend)
9. Library frontend: rootOnly + collapsible sections
10. BACKLOG.md additions
11. Commit design doc to both repos; push
