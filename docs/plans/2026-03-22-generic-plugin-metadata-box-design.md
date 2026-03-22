# Generic Plugin Metadata Box — Design

**Date:** 2026-03-22
**Status:** Approved
**Branch:** claude/epic-perlman

---

## Problem

The media detail page hardcodes a separate UI box for each metadata plugin (TMDB, MusicBrainz). Adding a new plugin requires manual frontend changes: a new typed interface, a new hardcoded box, new mutations. Plugin-specific code leaks into Chronicle core. TMDB data is treated as a first-class citizen in the API DTO (`TmdbMetaDto`, `TmdbMeta: TmdbMetaDto?`), violating the project's plugin-first architecture principle.

---

## Goals

- Zero frontend changes required when a new metadata plugin is installed
- Chronicle core has no knowledge of any plugin's data shape
- Plugin-specific information (display name, icon, fix match hint) comes from the plugin manifest
- All plugin metadata flows through a single generic channel
- Per-plugin Refresh, Fix Match, and Clear Match actions available on every plugin box

---

## Architecture Overview

Three layers change:

1. **Backend** — TMDB loses first-class DTO status; all plugin data flows through `PluginMetadata`. Plugin manifest gains branding fields. The refresh endpoint gains a plugin-scoped variant. `reidentify` is replaced by the plugin-scoped refresh with optional input override.

2. **Frontend data** — `MediaItem` TypeScript type drops `tmdbMeta`. `TmdbMeta` and `MusicBrainzMeta` interfaces deleted. `pluginMetadata: Record<string, unknown>` is the single channel for all plugin data.

3. **Frontend UI** — Hardcoded TMDB and MusicBrainz boxes replaced by a generic `PluginMetadataBox` component. `MediaDetailPage` loops over `pluginMetadata` entries and renders one box per plugin, with branding looked up from the installed plugins list.

---

## Section 1: Backend — MetadataJson

`MetadataJson` on `media_items` stores all plugin data under the plugin's own ID as the key:

```json
{
  "chronicle.plugin.tmdb": { "rating": 8.4, "genres": ["Drama"], ... },
  "chronicle.plugin.musicbrainz": { "externalId": "...", "cast": [...], ... },
  "fileScanner": { "filePath": "/media/...", "importedAt": "..." }
}
```

`ParseMetaJson` becomes simple: extract `"fileScanner"` (core system service, not a plugin) into its own typed DTO; pass every other key through to `PluginMetadata` as a raw `JsonElement`. No type-casting, no branching, no plugin awareness.

`_firstClassKeys` contains only `"fileScanner"`. No plugin ID ever appears in that set.

**Database:** Drop and recreate via a new EF migration. No backward compat shims needed.

---

## Section 2: Backend — PluginDto Branding

Each plugin's `manifest.json` declares:

```json
{
  "plugin_id": "chronicle.plugin.tmdb",
  "name": "TMDB",
  "version": "1.0.0",
  "iconUrl": "https://www.themoviedb.org/favicon.ico",
  "fixMatchHint": "Enter a TMDB ID (e.g. 550), typed ID (movie:550 · tv:1396), or URL"
}
```

Chronicle reads these at plugin load time. `PluginDto` gains:

```csharp
string? IconUrl
string? FixMatchHint
```

Chronicle never hardcodes a plugin's icon, display name, or fix match hint.

---

## Section 3: Backend — API Changes

### MediaItemDto

- Remove `TmdbMetaDto` record entirely
- Remove `TmdbMeta: TmdbMetaDto?` field from `MediaItemDto`
- `PluginMetadata: Dictionary<string, JsonElement>?` remains and now includes TMDB data

### Refresh endpoints

**Existing (unchanged):**
```
POST /api/v1/media/{id}/refresh
```
Refreshes all applicable plugins for the item. Stays as the "Refresh All" global action.

**New — plugin-scoped refresh:**
```
POST /api/v1/media/{id}/refresh/{pluginId}
Body: { "input": "optional override query" }
```
- No body → re-fetches from the named plugin using the existing stored external ID (Refresh)
- With `input` → searches the named plugin with the provided query, stores new match, fetches data (Fix Match)

This single endpoint replaces both the per-plugin Refresh and the `reidentify` endpoint. The existing `POST /media/{id}/reidentify` endpoint is removed.

### Clear external ID (unchanged)
```
DELETE /api/v1/media/{id}/external-ids/{source}
```
Already generic. `source` is the plugin ID (e.g. `chronicle.plugin.tmdb`).

---

## Section 4: Frontend — TypeScript Types

**Deleted:**
- `TmdbMeta` interface
- `MusicBrainzMeta` interface
- `MusicBrainzAdditionalImage` interface
- `tmdbMeta?: TmdbMeta | null` field on `MediaItem`

**`MediaItem` after change:**
```ts
export interface MediaItem {
  // ... existing fields unchanged ...
  externalIds: ExternalId[]
  fileScannerMeta?: FileScannerMeta | null
  pluginMetadata?: Record<string, Record<string, unknown>> | null
  refreshLogs?: RefreshLog[] | null
}
```

**`PluginDto` gains:**
```ts
iconUrl: string | null
fixMatchHint: string | null
```

---

## Section 5: Frontend — PluginMetadataBox Component

**Location:** `src/Chronicle.Web/src/components/ui/PluginMetadataBox.tsx`
**CSS module:** `PluginMetadataBox.module.css`

### Props

```ts
interface PluginMetadataBoxProps {
  pluginId: string
  mediaId: number
  data: Record<string, unknown>
  branding: {
    displayName: string
    iconUrl: string | null
    fixMatchHint: string | null
  }
}
```

### Internal state

- `fixMatchOpen: boolean` — toggles Fix Match panel
- `fixMatchInput: string` — controlled input value

### Mutations (all internal, not leaked to parent)

- **refreshMut** — `POST /media/{id}/refresh/{pluginId}` (no body)
- **fixMatchMut** — `POST /media/{id}/refresh/{pluginId}` with `{ input }`
- **clearMatchMut** — `DELETE /media/{id}/external-ids/{pluginId}`

All mutations invalidate `['media', mediaId]` and `['library']` on success.

### Layout

```
┌─────────────────────────────────────────────────────┐
│ [icon] Plugin Name          [✕ Clear] [⚙ Fix Match] [↻ Refresh] │
├─────────────────────────────────────────────────────┤
│ (Fix Match panel — shown when fixMatchOpen)          │
│  hint text with examples                             │
│  [_________________________] [Apply]                 │
│  (error message if failed)                           │
├─────────────────────────────────────────────────────┤
│ Field Label    value                                 │
│ Field Label    value                                 │
│ ...                                                  │
└─────────────────────────────────────────────────────┘
```

Fix Match panel behaviour:
- Focus input on open
- Enter key submits, Escape dismisses and clears input
- Error message shown inline on failure
- Hint text rendered from `branding.fixMatchHint`; fallback: `"Enter an ID or URL to search {displayName}"`

---

## Section 6: Frontend — Field Rendering

The box iterates `Object.entries(data)` and renders each field as a row. Rules:

| Value type | Rendering |
|---|---|
| `null` / `undefined` | Skip |
| Key starting with `_` | Skip (internal) |
| String — image URL (ends `.jpg/.png/.webp/.gif` or known CDN domain) | Clickable thumbnail + label |
| String — other | Text value |
| `number` | Display as-is |
| `boolean` | "Yes" / "No" |
| Array of strings | Tag chips |
| Array of objects | Each object as an indented sub-block |
| Nested object | Indented sub-block of rows |

**Key label formatting:** camelCase → Title Case
(`posterUrl` → "Poster Url", `voteAverage` → "Vote Average")

No plugin-specific field handling anywhere in this component.

---

## Section 7: Frontend — MediaDetailPage Changes

The page:
1. Fetches installed plugins list (`useQuery(['plugins'], getPlugins)`)
2. Builds `brandingMap: Record<string, { displayName, iconUrl, fixMatchHint }>`
3. Replaces hardcoded TMDB and MusicBrainz boxes with:

```tsx
{Object.entries(item.pluginMetadata ?? {}).map(([pluginId, data]) => (
  <PluginMetadataBox
    key={pluginId}
    pluginId={pluginId}
    mediaId={item.id}
    data={data}
    branding={brandingMap[pluginId] ?? { displayName: pluginId, iconUrl: null, fixMatchHint: null }}
  />
))}
```

**Removed from MediaDetailPage:**
- `clearMatchMut`, `clearMbMatchMut`, `reidentifyMut` mutations
- `fixMatchOpen`, `fixMatchInput` state
- `tmdbIds`, `otherIds`, `isTmdbSupported`, `tmdbHasRealId`, `tmdbSuppressed` derived values
- All TMDB box JSX (~200 lines)
- All MusicBrainz box JSX (~100 lines)

The global `↻ Refresh` strip remains — it calls `POST /media/{id}/refresh` (all plugins).
The File Scanner box remains — it is a core system service, not a plugin.

---

## Implementation Sequence

1. Backend — update plugin manifest schema, load `iconUrl` + `fixMatchHint` into `PluginDto`
2. Backend — update TMDB manifest with `iconUrl` + `fixMatchHint`
3. Backend — update MusicBrainz manifest with `iconUrl` + `fixMatchHint`
4. Backend — remove `TmdbMetaDto`, remove `TmdbMeta` from `MediaItemDto`, simplify `ParseMetaJson`
5. Backend — new plugin-scoped refresh endpoint `POST /media/{id}/refresh/{pluginId}`
6. Backend — remove `reidentify` endpoint
7. Backend — EF migration (drop + recreate affected data)
8. Frontend — update `PluginDto` TypeScript type (`iconUrl`, `fixMatchHint`)
9. Frontend — remove `TmdbMeta`, `MusicBrainzMeta` interfaces; remove `tmdbMeta` from `MediaItem`
10. Frontend — create `PluginMetadataBox` component + CSS module
11. Frontend — update `MediaDetailPage` to loop over `pluginMetadata`
12. Tests — update unit + integration tests for removed/changed endpoints
