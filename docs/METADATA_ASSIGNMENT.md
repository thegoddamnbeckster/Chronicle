# Metadata Assignment

Design document for Chronicle's per-field metadata source priority system.

---

## Table of Contents

- [Overview](#overview)
- [Metadata Assignment Page](#metadata-assignment-page)
- [Physical File Indicators](#physical-file-indicators)
- [Plugin Metadata Folds](#plugin-metadata-folds)
- [Background Tasks Page Refactor](#background-tasks-page-refactor)
- [File Scanner as a Plugin](#file-scanner-as-a-plugin)
- [Data Model](#data-model)
- [API](#api)
- [Implementation Order](#implementation-order)

---

## Overview

Chronicle stores metadata from multiple plugins simultaneously (TMDB, MusicBrainz, etc.), all
partitioned under `metadata_json`. Currently the first-class `MediaItem` fields (title, year,
poster, overview, etc.) are populated by whichever plugin ran enrichment last. There is no way to
say "use TMDB for posters, but MusicBrainz for descriptions".

Metadata Assignment introduces a **per-field source priority list**. For each media type and each
first-class field, the user (admin) configures an ordered list of plugins. The first plugin that
has a non-null value for that field wins. The second plugin is the first fallback, and so on.

**Key insight:** Metadata Assignment is a _display and serving concern only_ — it does not change
how enrichment runs. All plugins still enrich independently and store their data in `metadata_json`.
Assignment only affects how `metadata_json` values are promoted to `MediaItem` first-class fields
at read time.

When Chronicle acts as a metadata provider to external applications (Sonarr, Radarr, etc.), the
assigned priority determines what data it returns.

---

## Metadata Assignment Page

**Route:** `Settings → Metadata Assignment`

### Layout

Four columns:

| Col 1 — Media Type | Col 2 — Field | Col 3 — Priority List | Col 4 — Example |
|---|---|---|---|
| **Movies** (section header) | | | |
| | Title | [TMDB ↑↓] [FileScanner ↑↓] | _Fight Club_ |
| | Description | [TMDB ↑↓] [FileScanner ↑↓] | _An insomniac..._ |
| | Year | [TMDB ↑↓] [FileScanner ↑↓] | 1999 |
| | Poster | [TMDB ↑↓] [FileScanner ↑↓] | _(thumbnail)_ |
| | Backdrop | [TMDB ↑↓] | _(thumbnail)_ |
| | Runtime | [TMDB ↑↓] | 139 min |
| | Rating | [TMDB ↑↓] | 8.4 |
| | Genres | [TMDB ↑↓] | Drama, Thriller |
| | Cast | [TMDB ↑↓] | Brad Pitt, Edward Norton |
| | Directors | [TMDB ↑↓] | David Fincher |
| **TV Shows** (section header) | | | |
| | ... | ... | ... |

**Section delineation:** Each media type is a full-width header row with a distinct background
colour and larger font. Rows beneath it are indented. There is a visible divider line between
sections.

### Priority List (Col 3)

- Shows installed plugins that support this media type and field as a vertical ordered list
- Each entry has **↑** and **↓** buttons; top item has no ↑, bottom item has no ↓
- Order = priority: item 1 is primary, item 2 is first fallback, etc.
- Plugins not installed are not shown (list is dynamic)
- If only one plugin supports a field, up/down buttons are disabled

### Example (Col 4)

- Admin selects a representative media item once per media type (stored in `app_settings` as
  `metadata_assignment.example.{mediaTypeSlug}`)
- The example value is fetched live: Chronicle reads the primary plugin's data from that item's
  `metadata_json` and renders it exactly as it would appear in the library
- Poster/backdrop fields show a small thumbnail (max 80px wide)
- Text fields truncate at ~60 chars with a tooltip for full text
- If no example item is configured, show a "Select example item…" button that opens a media
  picker modal

### First-Class Fields by Media Type

These are the fields from `MediaMetadata` / `MediaItem` that can be assigned:

| Field | Internal key |
|---|---|
| Title | `title` |
| Sort Title | `sort_title` |
| Description | `overview` |
| Year | `year` |
| Poster Image | `poster_url` |
| Backdrop Image | `backdrop_url` |
| Runtime (minutes) | `runtime_minutes` |
| Rating | `rating` |
| Genres | `genres` |
| Cast | `cast` |
| Directors | `directors` |
| Tags | `tags` |

Not all fields apply to every media type. The page only shows applicable fields per type:

| Field | Movies | TV Shows | Music Artists | Albums | Tracks | Books | Audiobooks |
|---|---|---|---|---|---|---|---|
| Title | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Sort Title | ✓ | ✓ | ✓ | ✓ | | ✓ | ✓ |
| Description | ✓ | ✓ | ✓ | ✓ | | ✓ | ✓ |
| Year | ✓ | ✓ | | ✓ | ✓ | ✓ | ✓ |
| Poster | ✓ | ✓ | ✓ | ✓ | | ✓ | ✓ |
| Backdrop | ✓ | ✓ | ✓ | | | | |
| Runtime | ✓ | ✓ | | | ✓ | | ✓ |
| Rating | ✓ | ✓ | | ✓ | | ✓ | ✓ |
| Genres | ✓ | ✓ | ✓ | ✓ | | ✓ | ✓ |
| Cast | ✓ | ✓ | | | | | |
| Directors | ✓ | ✓ | | | | | |
| Tags | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

### Permissions

- **Read:** All authenticated users (to understand where data comes from)
- **Edit:** Admin users only (reordering and saving)

### Persistence

Stored in `app_settings` as a single JSON blob:

```
Key:   metadata_assignment.config
Value: { "movies": { "title": ["tmdb", "chronicle.plugin.fileScanner"], "poster_url": ["tmdb"], ... }, "tv": { ... }, ... }
```

The value is a `Dictionary<string, Dictionary<string, string[]>>` (mediaType → field → ordered
plugin IDs).

Default: every field defaults to the single plugin that supports that media type (or the first one
if multiple support it), in installation order.

---

## Physical File Indicators

Media items will display icons indicating whether physical files are present.

### Icons

| State | Icon | Meaning |
|---|---|---|
| Has physical file | HDD/disk icon | At least one file exists in the file scanner data |
| Metadata only | Cloud icon | No physical file; data from metadata source only |

Both icons may be shown simultaneously on parent-level items when children have mixed state.

### Logic

**Base items (leaf nodes, HierarchyLevel 2 or standalone HierarchyLevel 0):**
- Check `metadata_json → fileScanner` presence: if non-null and contains `filePaths`, show HDD icon
- Otherwise show cloud icon
- Mutually exclusive: a base item has one or the other

**Parent items (HierarchyLevel 0 or 1 with children):**
- Aggregate from all direct children:
  - If ALL children have physical files → HDD icon only
  - If NO children have physical files → cloud icon only
  - If MIXED → both HDD icon and cloud icon

**Bubbling:** The aggregation is computed at read time from children's states. It is not stored
separately; it is derived on the fly in the API response or computed in the frontend.

### Display placement

Icons appear in the upper-right corner of the media card (library grid/list view) and in the
media item header on the detail page, near the title/year line.

---

## Plugin Metadata Folds

### Media Detail Page

**Layout after this change:**

```
[ First-class fields — always visible ]
  Title, Year, Poster, Rating, Overview, Genres, Cast, etc.
  These are resolved using Metadata Assignment priority rules.

[ TMDB ▼ ]           ← fold, default open, state persisted
  (existing PluginMetadataBox contents)

[ MusicBrainz ▼ ]    ← fold, default open, state persisted
  (existing PluginMetadataBox contents)
```

The first-class block at the top is not in a fold. Plugin-specific boxes are folds.

### Fold State Persistence

Fold open/closed state is stored per-user in `preferences_json` under a `folds` key:

```json
{
  "showDiagnostics": false,
  "defaultFoldsOpen": true,
  "folds": {
    "media.42673.tmdb": false,
    "media.42673.musicbrainz": true,
    "backgroundTasks.tmdb": false
  }
}
```

**Key format:** `{context}.{itemId}.{pluginId}` for media detail folds,
`{context}.{pluginId}` for page-level folds (background tasks).

**API:** The existing `PATCH /api/v1/users/me/preferences` endpoint handles this. The frontend
sends individual fold key updates. No new endpoint needed.

**Reset setting:** In Settings → Appearance (or a new Preferences section), a toggle:
`Default folds open` (bool, default: true). A "Reset all folds" button sets all fold keys back
to the default. Both stored in `preferences_json`.

---

## Background Tasks Page Refactor

### Move Concurrent Threads to File Scanner Plugin Settings

`scan.max_concurrency` is currently displayed as a `ScanSettingsSection` on the Background Tasks
page. After the File Scanner plugin extraction (see below), this becomes a setting in the File
Scanner plugin's `PluginSettingsSchema`:

```csharp
new SettingDefinition
{
    Key          = "MaxConcurrency",
    Label        = "Max Concurrent Scan Folders",
    Description  = "How many scan folders run in parallel. Default: max(1, CPU cores ÷ 4).",
    Type         = SettingType.Number,
    Required     = false,
    DefaultValue = null,   // null = auto (CPU/4)
}
```

The `ScanSettingsSection` component is removed from BackgroundTasksPage.

### Group Background Tasks by Plugin

Currently background tasks are listed flat. After this change:

```
[ File Scanner ▶ ]   ← fold, default CLOSED, state persisted
  Scheduled Scan       CRON: 0 2 * * *    [ Run Now ]

[ TMDB ▶ ]           ← fold, default CLOSED, state persisted
  Fetch Missing        CRON: 0 4 * * *    [ Run Now ]
  Re-sync All          CRON: 0 3 * * 0    [ Run Now ]

[ MusicBrainz ▶ ]    ← fold, default CLOSED, state persisted
  Fetch Missing        CRON: 0 4 * * *    [ Run Now ]
```

- Each plugin is a collapsible section
- Default state: closed (to reduce visual noise on a page that may have many plugins)
- Fold state persisted using the same `preferences_json` mechanism described above
- Plugins with no background tasks are not shown

---

## File Scanner as a Plugin

The file scanner is currently deeply embedded in `Chronicle.Services` as `FileScanService`.
This must be extracted into a standalone plugin DLL: `Chronicle.Plugin.FileScanner`.

### Why

- Consistent with the plugin-first architecture principle
- Allows the file scanner to be updated independently of Chronicle core
- Plugin settings (scan folders, concurrency) live in the encrypted plugin settings store
  like all other plugins
- Makes it possible to have multiple scanner implementations or disable the scanner entirely

### New Project

```
W:\Scripts\Chronicle.Plugin.FileScanner\
├── Chronicle.Plugin.FileScanner.csproj
├── manifest.json
├── FileScannerPlugin.cs          # IImportProvider (or new IScannerPlugin) implementation
├── FileScanService.cs            # moved from Chronicle.Services
├── ScanGroupingService.cs        # moved from Chronicle.Services
├── FolderSignalExtractor.cs      # moved from Chronicle.Services/Scan/
├── TagSignalExtractor.cs         # moved from Chronicle.Services/Scan/
├── NfoSignalExtractor.cs         # moved from Chronicle.Services/Scan/
└── manifest.json
```

### Plugin Interface

The file scanner does not fit cleanly into `IMetadataProvider` or `IImportProvider`. It needs
a new interface: `IScannerPlugin` (in `Chronicle.Plugins`).

```csharp
public interface IScannerPlugin
{
    string PluginId { get; }
    string Name { get; }
    string Version { get; }
    string Author { get; }

    PluginSettingsSchema GetSettingsSchema();
    void Configure(IReadOnlyDictionary<string, string> settings);

    /// <summary>Returns configured scan root paths.</summary>
    IReadOnlyList<string> GetScanRoots();

    /// <summary>Preview scan: returns grouped candidates without persisting.</summary>
    Task<ScanPreviewResult> PreviewAsync(IReadOnlyList<string> roots, CancellationToken ct);

    /// <summary>Import previously previewed groups into the database.</summary>
    Task<ScanImportResult> ImportAsync(IReadOnlyList<ScanGroupImport> groups, CancellationToken ct);

    Task<bool> HealthCheckAsync(CancellationToken ct);
}
```

### Settings Schema

```csharp
new SettingDefinition { Key = "ScanRoots",       Label = "Scan Folders",             Type = SettingType.TextArea  },
new SettingDefinition { Key = "MaxConcurrency",  Label = "Max Concurrent Folders",    Type = SettingType.Number    },
new SettingDefinition { Key = "ScheduledCron",   Label = "Scheduled Scan Cron",       Type = SettingType.Text      },
new SettingDefinition { Key = "ScheduleEnabled", Label = "Enable Scheduled Scan",     Type = SettingType.Boolean   },
```

### Migration Path

1. Create `Chronicle.Plugin.FileScanner` project referencing Chronicle.Plugins + Chronicle.Core
2. Move scan services from `Chronicle.Services/Scan/` into the new project
3. `FileScanController` in Chronicle.API moves its scan-specific logic into the plugin;
   the controller becomes a thin proxy that calls `IScannerPlugin` via `PluginRegistry`
4. Scan folder storage (currently `scan_folders` DB table or similar) moves into plugin
   settings JSON — comma-separated roots in `ScanRoots` setting, encrypted at rest
5. `ScheduledScanService` in Chronicle.Services is replaced by a background task declared
   in the plugin's `manifest.json`
6. Remove `FileScanService`, `ScanGroupingService`, and scan signal extractors from
   `Chronicle.Services`

> **Note:** This is the most disruptive change in this document and should be implemented last,
> after the other features are stable.

---

## Data Model

### New: `metadata_field_assignments` (stored in `app_settings`)

No new table needed. The assignment config is stored as a JSON blob in `app_settings`:

```
Key:   metadata_assignment.config
Value: (see Persistence section above)
```

### Updated: `UserPreferences` (Chronicle.Core)

Add new fields to `UserPreferences.cs`:

```csharp
public class UserPreferences
{
    public bool? ShowDiagnostics { get; set; }
    public bool? DefaultFoldsOpen { get; set; }                         // new
    public Dictionary<string, bool>? Folds { get; set; }               // new
}
```

---

## API

### GET /api/v1/settings/metadata-assignment

Returns the current assignment config plus available plugins per media type:

```json
{
  "success": true,
  "data": {
    "assignments": {
      "movies": {
        "title":     ["tmdb"],
        "poster_url": ["tmdb"],
        "overview":  ["tmdb", "chronicle.plugin.fileScanner"]
      }
    },
    "availablePlugins": {
      "movies": [
        { "pluginId": "tmdb",   "name": "TMDB" },
        { "pluginId": "chronicle.plugin.fileScanner", "name": "File Scanner" }
      ]
    }
  }
}
```

**Auth:** Any authenticated user.

### PUT /api/v1/settings/metadata-assignment

Saves the full assignment config. Body: `{ "assignments": { ... } }`.

**Auth:** Admin only.

### PATCH /api/v1/users/me/preferences (existing, extended)

Already handles `preferences_json` updates. No changes needed — fold state uses this endpoint.

---

## Implementation Order

These features are independent enough to implement in stages:

1. **Physical file indicators** — smallest change; purely additive to media cards and detail page
2. **Plugin metadata folds** — additive UI change; fold state wires into existing preferences API
3. **Background tasks grouping + fold** — UI-only refactor of BackgroundTasksPage
4. **Metadata Assignment page** — new Settings page + `app_settings` key + enrichment read-path change
5. **Move concurrent threads to File Scanner settings** — depends on File Scanner plugin existing
6. **File Scanner as plugin DLL** — largest change; do last

---

*See also: `PLUGIN_SYSTEM.md`, `DATABASE_SCHEMA.md`, `UI_DESIGN.md`*
