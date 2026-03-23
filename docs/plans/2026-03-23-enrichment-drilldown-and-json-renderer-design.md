# Enrichment Drill-Down & Smart JSON Renderer — Design

**Date:** 2026-03-23
**Status:** Approved

---

## Overview

Two related features that together make enrichment failures diagnosable and plugin metadata readable:

1. **Enrichment Drill-Down Page** — clickable counts in the Background Tasks enrichment table open a dedicated, library-style page showing every item in that state with full diagnostic detail.
2. **Smart JSON Renderer** — replaces the raw `JSON.stringify` fallback in `PluginMetadataBox` with a recursive tree component that renders nested structures visually, turns URLs into links, image URLs into thumbnails, and IDs into badges.

---

## Feature 1: Enrichment Drill-Down Page

### Goal

Answer the question "why does TMDB have 1081 NotFound items?" — show exactly which items weren't matched, what was searched for, what candidates were returned (with scores), and what to do to fix each one.

### Data Model Change

Add one nullable column to `media_item_enrichment_status`:

```sql
ALTER TABLE media_item_enrichment_status
  ADD COLUMN diagnostics_json TEXT NULL;
```

The `DiagnosticsJson` column stores a JSON blob written on every enrichment attempt:

```json
{
  "searchQuery": "Alanis Morissette",
  "searchYear": 1974,
  "failureReason": "NotFound",
  "candidatesReturned": 0,
  "topCandidates": [
    {
      "title": "Alanis Morissette",
      "year": 1974,
      "externalId": "artist:12345",
      "titleScore": 60,
      "yearScore": 40,
      "totalScore": 100,
      "isMatch": true
    }
  ],
  "scannerSignals": {
    "hasFolderName": true,
    "hasNfo": false,
    "hasAudioTags": true,
    "hasYearInFolder": true,
    "hasLocalPoster": true,
    "folderPath": "H:/Music/Alanis Morissette",
    "detectedName": "Alanis Morissette",
    "detectedYear": 1974,
    "confidenceScore": 0.87
  }
}
```

**Migration:** `20260323_AddEnrichmentDiagnostics` — adds `diagnostics_json TEXT NULL` to `media_item_enrichment_status`. Non-breaking; existing rows get NULL and will be populated on the next enrichment run.

**Existing items:** Run "Reset All → Run Now" for any plugin to populate diagnostics for items that previously returned NotFound/Failed/Exhausted.

### Service Changes

**`MetadataEnrichmentService`** — after each enrichment attempt, build and save `DiagnosticsJson`:

- Before the search: capture `searchQuery`, `searchYear`, scanner signals from `metadata_json.fileScanner`
- After the search: capture `candidatesReturned`, `topCandidates` (up to 5, with per-candidate title/year/score breakdown), `failureReason`
- Write to `row.DiagnosticsJson` before saving

`ScoreCandidate()` already computes title + year points in `FileScanService`; the same logic needs to be surfaced in `MetadataEnrichmentService` so scores can be stored. Extract a shared `ScoreCandidate(title, year, candidateTitle, candidateYear)` helper (or duplicate the simple logic inline).

### New API Endpoint

```
GET /api/v1/enrichment/{pluginId}/items
  ?status=NotFound|Failed|Exhausted|Pending|Completed|Skipped
  &page=1
  &pageSize=50
  &search=<optional name filter>
```

Returns paginated list of enrichment items with joined media item data:

```json
{
  "success": true,
  "data": [
    {
      "enrichmentId": 123,
      "mediaItemId": 456,
      "name": "Alanis Morissette",
      "year": 1974,
      "mediaType": "music",
      "hierarchyLevel": 0,
      "posterUrl": "/images/poster.jpg",
      "externalId": null,
      "status": "NotFound",
      "errorMessage": null,
      "retryCount": 3,
      "lastAttemptedAt": "2026-03-20T03:00:00Z",
      "diagnostics": { ... }
    }
  ],
  "pagination": { "page": 1, "pageSize": 50, "total": 1081, "totalPages": 22 }
}
```

### New Page: `/settings/enrichment/:pluginId`

**Route params:** `pluginId` — e.g., `chronicle.plugin.tmdb`
**Query params:** `status` — pre-selects the filter tab

**Layout:**

```
Enrichment — TMDB                          [← Back to Background Tasks]

[All] [Pending 1805] [NotFound 1081] [Failed 3] [Exhausted 0] [Skipped 0]

[Search by name…]                          [Reset All Filtered] [Skip All Filtered]

┌─────────────────────────────────────────────────────────────────────────┐
│ [Poster]  Alanis Morissette (1974)  •  Music Artist  •  Level 0        │
│           H:/Music/Alanis Morissette                                    │
│                                                                         │
│  SCANNER SIGNALS                                                        │
│  ✓ Folder name detected: "Alanis Morissette"                            │
│  ✓ Audio tags present                                                   │
│  ✓ Year in folder: 1974                                                 │
│  ✗ NFO file: not found                                                  │
│  ✓ Local poster: found                                                  │
│  Confidence: 87%  ████████████████░░░░                                  │
│                                                                         │
│  ENRICHMENT DIAGNOSTICS                                                 │
│  Searched: "Alanis Morissette" (year: 1974)   Last attempt: 3 days ago  │
│  Result: 0 candidates returned                                          │
│                                                                         │
│  WHY IT FAILED                                                          │
│  The plugin returned no results for this search query. Check that the   │
│  plugin API key is configured and the item name is spelled correctly.    │
│                                                                         │
│  [Fix Match]  [Skip]  [Reset & Retry]  [View in Library →]             │
└─────────────────────────────────────────────────────────────────────────┘
```

**For Failed/Exhausted** items, the card also shows:
- Error message verbatim
- Retry count / max retries
- Top candidates returned with score breakdown:

```
TOP CANDIDATES RETURNED
  #1  Alanis Morissette (1995)  — title: 60pts  year: 0pts  total: 60/100  [not matched — year gap]
  #2  Alanis (2021)             — title: 32pts  year: 0pts  total: 32/100
```

**Action buttons per card:**
- **Fix Match** — opens the existing Fix Match input (same as media detail page); only on hierarchy level 0 items
- **Skip** — marks as Skipped for this plugin; POST `/enrichment/{pluginId}/items/{id}/skip`
- **Reset & Retry** — resets to Pending; POST `/enrichment/{pluginId}/reset` with scope `"single"` and `mediaItemId`
- **View in Library →** — navigates to `/media/{mediaItemId}`

**Bulk actions (top bar):**
- **Reset All Filtered** — resets every item currently matching the active status filter
- **Skip All Filtered** — skips every item currently matching the active status filter

### Navigation

In `BackgroundTasksPage`, each count cell in the enrichment table becomes a `<Link>`:

```
NotFound: <Link to={`/settings/enrichment/${pluginId}?status=NotFound`}>1081</Link>
```

---

## Feature 2: Smart JSON Renderer (`<JsonTree>`)

### Goal

Replace the `<pre>{JSON.stringify(value, null, 2)}</pre>` fallback in `PluginMetadataBox` with a component that renders nested JSON as a visual, interactive tree — without requiring any plugin-specific code.

### Component: `src/components/JsonTree.tsx`

**Props:**
```typescript
interface JsonTreeProps {
  data: unknown
  depth?: number          // current nesting depth (default 0)
  onImageClick?: (url: string) => void  // route image clicks to page-level lightbox
}
```

**Rendering rules (applied recursively):**

| Value type | Detected by | Rendered as |
|------------|-------------|-------------|
| `null` / `undefined` | typeof | Greyed `—` |
| Boolean | typeof | `Yes` / `No` badge |
| Number | typeof | Plain text |
| Image URL string | `isImageUrl()` (reuse from imageExtractor) | Inline `<img>` thumbnail; click → lightbox |
| Non-image URL string | starts with `http` / `https` | `<a href target="_blank">` link |
| ID-like string/number | key name ends in `id`, `Id`, `ID`, `mbid`, `uuid` | Monospace badge |
| Plain string | fallback | Plain text |
| Array (empty) | length === 0 | Greyed `(empty)` |
| Array of primitives | all items non-object | Comma-joined inline |
| Array of objects | items are objects | Numbered list, each item rendered as nested object |
| Object | typeof === 'object' | Collapsible labelled section |

**Collapsible behaviour:**
- Objects/arrays with >3 keys or items start collapsed (toggle with chevron)
- Objects/arrays with ≤3 keys start expanded
- Root call (depth 0) always starts expanded

**Key label formatting:**
- Reuse `toLabel()` from `imageExtractor.ts` (converts `camelCase`/`snake_case` → "Title Case")
- IDs kept visible as-is (not hidden, not prettified)

**Integration point:**
In `PluginMetadataBox.renderValue()`, replace the `typeof value === 'object'` branch:

```typescript
// Before:
return <pre ...>{JSON.stringify(value, null, 2)}</pre>

// After:
return <JsonTree data={value} depth={1} onImageClick={onImageClick} />
```

The `onImageClick` prop passes image clicks up to the page-level lightbox automatically.

### CSS: `src/components/JsonTree.module.css`

Key styles:
- `.node` — one key-value row, padding-left scales with depth
- `.key` — label, muted colour, monospace for ID keys
- `.collapseToggle` — small chevron button, no background
- `.idBadge` — monospace, subtle background, rounded
- `.nullValue` — greyed-out
- `.boolBadge` — Yes (green tint) / No (red tint)
- `.thumbnail` — max 130×185px, object-fit contain (matches existing thumbnail style)
- `.link` — accent colour, underline on hover

---

## Files to Create / Modify

### Backend

| File | Change |
|------|--------|
| `Chronicle.Core/Models/MediaItemEnrichmentStatus.cs` | Add `DiagnosticsJson string?` property |
| `Chronicle.Data/Migrations/20260323_AddEnrichmentDiagnostics.cs` | `ALTER TABLE` migration |
| `Chronicle.Services/MetadataEnrichmentService.cs` | Build + save diagnostics on every attempt |
| `Chronicle.API/DTOs/EnrichmentDTOs.cs` | Add `EnrichmentItemDto`, `EnrichmentDiagnosticsDto` |
| `Chronicle.API/Controllers/EnrichmentController.cs` | Add `GET /{pluginId}/items` endpoint |

### Frontend

| File | Change |
|------|--------|
| `src/components/JsonTree.tsx` | New component |
| `src/components/JsonTree.module.css` | New styles |
| `src/components/PluginMetadataBox.tsx` | Replace object fallback with `<JsonTree>` |
| `src/api/enrichment.ts` | Add `getEnrichmentItems()` function |
| `src/pages/settings/EnrichmentDrillDownPage.tsx` | New page |
| `src/pages/settings/EnrichmentDrillDownPage.module.css` | New styles |
| `src/pages/settings/BackgroundTasksPage.tsx` | Count cells → `<Link>` |
| `src/App.tsx` (or router file) | Register new route `/settings/enrichment/:pluginId` |

---

## Out of Scope

- Plugin-specific field renderers (per user: "making plugins more complicated sounds bad")
- Bulk export of enrichment results
- In-place metadata editing from the drill-down page (use Fix Match / View in Library instead)
