# Per-Plugin Background Tasks & Plugin Branding — Design

**Date:** 2026-03-21
**Status:** Approved, ready for implementation
**Branch:** `claude/epic-perlman`

---

## Problem

The existing background tasks page shows two generic tasks — "Metadata Enrichment" and "Metadata Refresh" — that both work across all installed plugins. The names are confusing (they sound like the same thing), there is no visual connection between a task and the plugin responsible for it, and there is no per-plugin schedule control.

---

## Goals

1. Each metadata plugin owns its own scheduled background tasks.
2. Task names clearly describe what happens ("Fetch Missing Metadata", "Re-sync All Metadata").
3. Task cards in the UI are visually branded with the plugin's icon and colours.
4. Plugin branding is defined by the plugin author in `manifest.json` — not sampled at runtime.
5. Non-plugin (system) tasks retain a neutral Chronicle style.
6. The architecture is extensible: future plugins can declare fully custom tasks beyond the two well-known types.

---

## Decision: Option C2 (Hybrid)

Plugin declares tasks **and** branding in `manifest.json`. For the two well-known task types, Chronicle executes them internally using its own services filtered to the declaring plugin. For future custom task types, plugins implement an `IPluginTask` interface and Chronicle invokes `RunAsync()` on schedule.

This removes the need for any changes to `IMetadataProvider` — branding and task metadata are purely a manifest concern.

---

## Section 1 — Manifest Schema

`manifest.json` gains two optional top-level fields for branding and an optional `background_tasks` array.

```json
{
  "plugin_id": "chronicle.plugin.musicbrainz",
  "name": "MusicBrainz",
  "version": "1.0.0",
  "author": "Chronicle Contributors",
  "description": "...",
  "min_chronicle_version": "0.1.0",
  "entry_type": "Chronicle.Plugin.MusicBrainz.MusicBrainzMetadataProvider",
  "iconUrl": "https://musicbrainz.org/favicon.ico",

  "brandColorLight": "#BA478F",
  "brandColorDark":  "#CF6BAA",

  "background_tasks": [
    {
      "task_id":       "fetch-missing-metadata",
      "display_name":  "Fetch Missing Metadata",
      "description":   "Looks up metadata from MusicBrainz for newly imported items that don't have it yet.",
      "default_cron":  "0 4 * * *",
      "default_enabled": true
    },
    {
      "task_id":       "resync-all-metadata",
      "display_name":  "Re-sync All Metadata",
      "description":   "Re-downloads all MusicBrainz metadata to pick up corrections and updates.",
      "default_cron":  "0 3 * * 0",
      "default_enabled": false
    }
  ]
}
```

### Manifest field reference

| Field | Type | Required | Notes |
|---|---|---|---|
| `plugin_id` | string | Yes | Reverse-domain ID, e.g. `chronicle.plugin.tmdb` |
| `name` | string | Yes | Display name shown in the UI |
| `version` | string | Yes | Semver |
| `author` | string | Yes | |
| `description` | string | No | Shown in catalog and installed plugins list |
| `min_chronicle_version` | string | Yes | Minimum Chronicle version required |
| `entry_type` | string | Yes | Fully-qualified class name implementing a plugin interface |
| `iconUrl` | string | No | URL of the plugin/service icon (favicon recommended). Used on the Plugins page and task cards. |
| `brandColorLight` | string | No | Hex colour for light-mode UI accents (`#RRGGBB`). Falls back to Chronicle teal if absent. |
| `brandColorDark` | string | No | Hex colour for dark-mode UI accents. Falls back to Chronicle teal if absent. |
| `background_tasks` | array | No | List of background tasks this plugin declares. Omit entirely if the plugin has no scheduled work. |

### Background task declaration fields

| Field | Type | Required | Notes |
|---|---|---|---|
| `task_id` | string | Yes | Either a well-known ID (see below) or a custom ID for `IPluginTask` implementations |
| `display_name` | string | Yes | Shown as the task heading in the UI |
| `description` | string | No | Shown as the task subheading |
| `default_cron` | string | Yes | Default cron schedule (5-field, UTC). Users can override via the UI. |
| `default_enabled` | bool | Yes | Whether the task is enabled when first installed |

### Well-known task IDs

| `task_id` | What Chronicle does |
|---|---|
| `fetch-missing-metadata` | Calls `IMetadataEnrichmentService.EnrichAsync(pluginId)` — processes the enrichment queue for this plugin, fetching metadata for items that don't have it yet |
| `resync-all-metadata` | Calls `IMetadataRefreshService.RefreshAsync(pluginId)` — re-downloads metadata for all library items that have already been matched to this plugin |

For well-known task IDs, the plugin declares the task in the manifest but does **not** need to implement any extra interface. Chronicle handles execution.

---

## Section 2 — Data Model

### Migration: add `plugin_id` to `background_tasks`

```sql
ALTER TABLE background_tasks
  ADD COLUMN plugin_id TEXT NULL
  REFERENCES plugins(PluginId) ON DELETE CASCADE;
```

- System tasks (`scheduled-scan`, `duplicate-cleanup`, etc.) leave `plugin_id` as `NULL`.
- Per-plugin tasks store the declaring plugin's `plugin_id`.
- `ON DELETE CASCADE`: uninstalling a plugin automatically removes its background task rows.

### Task ID namespacing

Per-plugin task IDs are stored namespaced to avoid collisions:

```
{plugin_id}:{task_id}
e.g. chronicle.plugin.musicbrainz:fetch-missing-metadata
```

System task IDs are unchanged (no prefix).

---

## Section 3 — Plugin Lifecycle

### On plugin install / enable

The plugin loader reads `background_tasks` from the manifest and **upserts** rows into `background_tasks`:
- If a row for that namespaced task ID already exists, leave user-configured cron/enabled values intact.
- If the row is new, seed it from `default_cron` and `default_enabled`.
- Store `plugin_id` on each row.

### On plugin uninstall

`ON DELETE CASCADE` removes the task rows automatically. No explicit cleanup needed.

### On plugin disable

Task rows remain but the scheduler skips disabled plugins' tasks (or the tasks themselves can be toggled off by the user independently).

---

## Section 4 — Task Execution

The `TaskSchedulerService` resolves how to run a task based on its namespaced ID:

1. Strip the `{plugin_id}:` prefix to get the bare `task_id`.
2. If `task_id` is a well-known ID, delegate to the appropriate Chronicle service with the `plugin_id` as a filter parameter.
3. If `task_id` is not well-known, look up the plugin assembly for a class implementing `IPluginTask` where `TaskId == task_id`, then call `RunAsync(ct)`.

### IPluginTask interface (for custom tasks)

```csharp
/// <summary>
/// Implement this interface in your plugin assembly to register a custom
/// background task. Chronicle will discover it automatically at plugin load
/// time and wire it to the task declared in your manifest.json.
/// </summary>
public interface IPluginTask
{
    /// <summary>
    /// Must match the task_id declared in manifest.json (without the plugin prefix).
    /// </summary>
    string TaskId { get; }

    Task RunAsync(CancellationToken ct);
}
```

### Removing the old global tasks

The existing `MetadataEnrichmentScheduledTask` and the global metadata refresh scheduled task are **removed**. Their work is now performed by per-plugin task rows seeded from each plugin's manifest.

---

## Section 5 — API / DTO Changes

`BackgroundTaskDto` gains plugin branding fields (nullable):

```csharp
public record BackgroundTaskDto(
    string TaskId,
    string DisplayName,
    string Description,
    string CronExpression,
    bool IsEnabled,
    bool IsRunning,
    bool? LastRunSucceeded,
    DateTime? LastRunAt,
    DateTime? NextRunAt,
    string? LastErrorMessage,
    // New:
    string? PluginId,
    string? PluginName,
    string? PluginIconUrl,
    string? BrandColorLight,
    string? BrandColorDark
);
```

The `GET /api/v1/background-tasks` endpoint populates these via a join to the `plugins` table using `plugin_id`.

---

## Section 6 — Frontend

### Task card layout

Each card displays:
- **Top-left**: plugin icon (16×16, from `pluginIconUrl`). Absent for system tasks.
- **Heading**: `{PluginName} · {DisplayName}` for plugin tasks; `{DisplayName}` for system tasks.
- **Border**: `brandColorDark` (current theme is dark). System tasks use Chronicle teal (`var(--color-accent)`).
- **Background tint**: `brandColorDark` at 8% opacity. System tasks use a neutral tint.

### System tasks

Tasks with `pluginId == null` receive the existing neutral Chronicle styling — no icon, no custom colour.

---

## Section 7 — Plugin Developer Documentation

`docs/PLUGIN_AUTHORING.md` will be created as a comprehensive guide covering:

1. **Overview** — what Chronicle plugins are and the available plugin types
2. **Manifest reference** — every field with types, requirements, and examples
3. **Plugin interfaces** — `IMetadataProvider`, `IWidgetPlugin`, `IFileScannerPlugin`, `IPluginTask`
4. **Background tasks** — well-known task IDs vs custom tasks, branding guidelines
5. **Branding guidelines** — colour format, recommended contrast ratios, light vs dark
6. **Build & packaging** — how to produce the ZIP (what to include/exclude)
7. **Publishing a GitHub release** — steps to make the plugin installable from the catalog
8. **Full worked example** — MusicBrainz as the reference implementation

---

## What is NOT changing

- `IMetadataProvider` interface — no new methods added
- Plugin DLL isolation model — unchanged
- Enrichment queue (`media_item_enrichment_status`) — unchanged, just filtered by plugin
- Existing plugin catalog install flow — unchanged
- User-configured cron schedules — preserved across plugin reinstalls (upsert, not replace)
