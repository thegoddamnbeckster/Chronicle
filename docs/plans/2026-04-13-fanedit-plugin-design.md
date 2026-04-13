# FanEdit Plugin — Design

**Date:** 2026-04-13
**Status:** Approved

---

## Overview

This document covers three related pieces of work that together deliver fan edit support in Chronicle:

1. **"Fan Edits" media type** — a new first-class media type seeded via DB migration
2. **Chronicle.Plugin.FanEdit** — a metadata provider that scrapes fanedit.org (IFDB)
3. **Type-switching** — a UI + API feature allowing an item's media type to be changed with a full data reset

---

## 1. Fan Edits Media Type

A new row is seeded into `media_types` via a DB migration:

| Field | Value |
|---|---|
| Name | Fan Edits |
| Slug | `fanedits` |
| Hierarchy levels | 1 (no seasons/episodes) |
| Interaction verbs | watched / rewatch |
| Progress units | minutes |

Fan edits are a **completely separate type from movies** — not a subtype, not a fallback. TMDB and other movie plugins never touch items of this type. The FanEdit plugin exclusively declares support for `"fanedits"`.

### How items become "Fan Edits"

There is no automatic detection — fan edit files are visually identical to regular movie files. The designation is always manual:

- **Scan folder** — the user configures a scan folder with "Fan Edits" as the media type; all files scanned from it receive the `fanedits` type
- **Add Media** — the user selects "Fan Edits" from the type dropdown when adding manually
- **Type-switching** — existing items can be moved from another type (see section 3)

The practical expectation is that fan edits live in a dedicated directory, separate from regular movies.

---

## 2. Chronicle.Plugin.FanEdit

### Location

`W:\Scripts\Chronicle.Plugin.FanEdit\` — a standalone .NET 9 class library, same pattern as Chronicle.Plugin.TMDB and Chronicle.Plugin.MusicBrainz.

### Project structure

```
Chronicle.Plugin.FanEdit/
├── Chronicle.Plugin.FanEdit.csproj
├── README.md
├── manifest.json
├── FanEditMetadataProvider.cs        IMetadataProvider entry point
├── FanEditAuthService.cs             WordPress login → session cookie
├── FanEditScraper.cs                 HtmlAgilityPack HTML parsing
├── FanEditRateLimiter.cs             SemaphoreSlim + Stopwatch throttle
└── Models/
    ├── FanEditEntry.cs
    ├── FanEditSearchResult.cs
    └── FanEditTechSpecs.cs
```

### .csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>Chronicle.Plugin.FanEdit</AssemblyName>
    <RootNamespace>Chronicle.Plugin.FanEdit</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                      Private="false" ExcludeAssets="runtime" />
    <ProjectReference Include="..\Chronicle\src\Chronicle.Core\Chronicle.Core.csproj"
                      Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="HtmlAgilityPack" Version="1.11.*" />
  </ItemGroup>
  <ItemGroup>
    <None Update="manifest.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

### manifest.json

```json
{
  "plugin_id":             "chronicle.plugin.fanedit",
  "name":                  "FanEdit (IFDB)",
  "version":               "1.0.0",
  "author":                "Chronicle Contributors",
  "description":           "Fetches fanedit metadata from the Internet Fan Edit Database (fanedit.org). Requires a registered fanedit.org account. Please use responsibly — a minimum 1-second delay between requests is enforced.",
  "min_chronicle_version": "0.1.0",
  "entry_type":            "Chronicle.Plugin.FanEdit.FanEditMetadataProvider",
  "iconUrl":               "https://www.fanedit.org/favicon.ico",
  "brandColorLight":       "#8B1A1A",
  "brandColorDark":        "#C0392B",
  "fixMatchHint":          "Enter a fanedit.org URL (e.g. https://www.fanedit.org/my-edit/) or a bare IFDB numeric ID",
  "background_tasks": [
    {
      "task_id":         "fetch-missing-metadata",
      "display_name":    "Fetch Missing Metadata",
      "description":     "Looks up IFDB metadata for fan edits that don't have it yet.",
      "default_cron":    null,
      "default_enabled": false,
      "schedulable":     false,
      "run_confirmation": {
        "title":   "Fetch fan edit metadata?",
        "message": "fanedit.org is a small community site maintained by volunteers. This task makes one HTTP request per fan edit with a minimum 1-second delay between each — on a large library this will take a long time. Please run this sparingly, not more than a few times per week."
      }
    },
    {
      "task_id":         "resync-all-metadata",
      "display_name":    "Re-sync All Metadata",
      "description":     "Re-fetches IFDB metadata for all fan edits to pick up updated descriptions, ratings, and images.",
      "default_cron":    null,
      "default_enabled": false,
      "schedulable":     false,
      "run_confirmation": {
        "title":   "Re-sync all fan edit metadata?",
        "message": "This will re-fetch IFDB metadata for every fan edit in your library. fanedit.org is a small community site — each request has a minimum 1-second delay. On a large library this will take a very long time. Please use this sparingly."
      }
    }
  ]
}
```

### New generic manifest fields

These fields are added to the background task definition schema and apply to any plugin:

| Field | Type | Meaning |
|---|---|---|
| `schedulable` | `bool` (default `true`) | When `false`, the cron editor is hidden in the Background Tasks UI — the task can only be triggered manually |
| `run_confirmation` | `{ title, message }` \| `null` | When present, the Run Now button shows a confirmation modal with this content before firing |

Both fields are stored in the `background_tasks` DB table and surfaced in the `BackgroundTask` DTO / API response.

### Settings schema

| Key | Label | Type | Required | Default | Notes |
|---|---|---|---|---|---|
| `username` | fanedit.org Username | Text | Yes | — | Registered fanedit.org account |
| `password` | fanedit.org Password | Password | Yes | — | Stored encrypted; never logged |
| `request_delay_ms` | Request Delay (ms) | Number | No | `1000` | Floor: 1000 ms enforced in code |
| `user_agent` | User-Agent String | Text | No | Chrome UA | Override HTTP User-Agent |

### GetSupportedMediaTypes

Returns `"fanedits"` only. No other media type.

### Authentication flow

1. `GET https://www.fanedit.org/wp-login.php` — extract `_wpnonce` hidden field
2. `POST https://www.fanedit.org/wp-login.php` with credentials + nonce — persist resulting `CookieContainer`
3. If mid-run 302 to login page detected — re-authenticate once, retry original request. If second auth fails, throw.
4. Credentials are never written to any log at any level.

### Rate limiting

All outbound HTTP calls (login, search, detail, image) go through `ThrottleAsync` before being issued:

```csharp
private readonly SemaphoreSlim _gate = new(1, 1);
private Stopwatch _lastRequest = Stopwatch.StartNew();
private int _delayMs; // max(configured, 1000)

private async Task ThrottleAsync(CancellationToken ct)
{
    await _gate.WaitAsync(ct);
    try
    {
        var elapsed = _lastRequest.ElapsedMilliseconds;
        if (elapsed < _delayMs)
            await Task.Delay((int)(_delayMs - elapsed), ct);
        _lastRequest.Restart();
    }
    finally { _gate.Release(); }
}
```

The floor of 1,000 ms is hard-coded and cannot be bypassed through configuration.

### SearchAsync

`GET https://www.fanedit.org/ifdb/?s={url-encoded-query}&post_type=fanedit`

Tries all `context.AltTitles` plus `context.Name`. Scoring per candidate:

| Signal | Score |
|---|---|
| Exact title match (normalised, lower-case) | +40 |
| Fuzzy title match (Levenshtein ≤ 20%) | +20 |
| Year exact match | +20 |
| Year ±1 | +10 |
| Year mismatch > 1 | −10 |
| Source material title in query | +10 |

Acceptance threshold: **50**. Returns up to 10 candidates sorted by descending score.

### GetByIdAsync

Accepted input formats:

| Input | Resolution |
|---|---|
| `https://www.fanedit.org/{slug}/` | Fetch detail page directly |
| `fanedit:{slug}` | Prepend base URL |
| `fanedit:{integer}` | Try `/ifdb/{id}/` |
| Bare integer | Try as numeric IFDB ID |
| Bare slug | Prepend `https://www.fanedit.org/` |

Detail page extraction priority:
1. OpenGraph / Schema.org meta tags (`og:title`, `og:description`, `og:image`)
2. Schema.org JSON-LD (`name`, `description`, `datePublished`, `author`)
3. Definition list key-value pairs (`Editor:`, `Runtime:`, `Video:`, `Audio:`, `Type:`)
4. Free-text body (Changes section, Editor's Notes)
5. Gallery section — all `<img>` elements as `AdditionalImages`

Any field not found on the page is silently set to `null` — never throws.

### MediaMetadata mapping

| IFDB field | `MediaMetadata` property |
|---|---|
| Fanedit title | `Title` |
| Description | `Overview` |
| Release year | `Year` |
| Runtime | `RuntimeMinutes` |
| Primary cover | `PosterUrl` |
| Screenshots | `AdditionalImages` |
| Genre tags | `Genres` |
| Community rating | `Rating` (0–10) |
| Tags | `Tags` |
| Everything else | `ExtendedData` (JSON) |

`ExtendedData` captures: original title/year/IMDb ID, editor username + profile URL, fan edit type, IFDB categories, tech specs (codec/resolution/container), changes list, cut/addition counts, IFDB rating details, awards, published date, distribution links.

### External ID format

`fanedit:{slug}` (preferred — stable and human-readable) or `fanedit:{numeric-id}`.
Stored in `media_external_ids` with `Source = "fanedit"`.

### Error handling

| Condition | Behaviour |
|---|---|
| Login failure | `HealthCheckAsync` → `false`; search/fetch throw descriptively |
| Session expired mid-run | Re-auth once, retry. Fail → throw. |
| HTTP 404 on detail page | Return `null` from `GetByIdAsync` |
| HTTP 429 / 503 | Back off `delay * 3`, retry once |
| HTML field missing | Log warning, field = `null`, continue |
| Network timeout | 30 s; rethrow as `HttpRequestException` with context |
| `CancellationToken` | Propagate immediately |

### Enrichment Status integration

Because `FanEditMetadataProvider` is a standard `IMetadataProvider`, it appears as a row in the Enrichment Status table on the Background Tasks page automatically once installed. No extra work required.

### Branding

| Mode | Colour |
|---|---|
| Light | `#8B1A1A` |
| Dark | `#C0392B` |

---

## 3. Type-Switching

### API

```
POST /api/v1/media/{id}/change-type
Authorization: Bearer {admin-jwt}
Content-Type: application/json

{ "mediaTypeId": 5 }
```

This is a **dedicated endpoint**, not a generic PATCH, because changing type has cascading side effects.

### Server logic

1. Load item. If `parent_id` is not null → return `400` with:
   ```json
   { "code": "CHANGE_TYPE_USE_ROOT", "parentId": 42 }
   ```
   The client navigates to the root item.

2. Compute actual tree depth (item + all descendants).
   If `target_type.hierarchy_levels < actual_depth` → return `400`:
   ```json
   { "code": "INCOMPATIBLE_TYPE", "message": "..." }
   ```

3. Atomically (single transaction):
   - Recursively collect all item IDs in the tree
   - Set `media_type_id` on all items
   - Delete all `media_enrichment_status` rows for all items
   - Delete all `media_external_ids` rows for all items
   - Set `metadata_json = null` on all items

4. Return `200` with the updated root item DTO.

### Why a full reset

When a type changes, the item is a fundamentally different thing in the library. A fan edit is not the source film. All previous enrichment data (TMDB metadata, external IDs, poster, overview) belongs to the old identity and must be discarded so the new type's plugins start with a clean slate.

After the reset, items with no enrichment record are implicitly **pending** for any plugin that supports the new type. The user presses Run Now on the appropriate plugin's fetch task to enrich them.

### Compatibility rule

Two types are compatible if their `hierarchy_levels` values match. The type-switcher dropdown only lists compatible target types — the server-side check is a safety net.

### Child item behaviour

If a user navigates to a child item (season, episode) and opens the Change Type control, the UI detects the `CHANGE_TYPE_USE_ROOT` response and redirects to the root item's Change Type UI, where the full-tree switch can be performed.

### UI

- "Change Type" button in the media detail header, alongside Delete. Admin-only.
- Opens a dropdown listing only compatible types (same `hierarchy_levels`). Current type is excluded.
- Confirmation dialog: *"This will reset all metadata, enrichment status, and external IDs for this item and all N descendants. This cannot be undone. Continue?"*
- On success: reload the same item page; it now shows as unenriched under the new type.

---

## Implementation sequence

1. **Generic manifest fields** — add `schedulable` and `run_confirmation` to `BackgroundTask` model, DTO, DB table, and Background Tasks page UI
2. **DB migration** — seed "Fan Edits" media type
3. **Change-type API** — `POST /media/{id}/change-type` endpoint + service method
4. **Change-type UI** — button + dropdown + confirmation on media detail page
5. **Chronicle.Plugin.FanEdit** — project scaffold, then implementation in order: `FanEditRateLimiter` → `FanEditAuthService` → `FanEditScraper` → `FanEditMetadataProvider` → `manifest.json`
6. **Tests** — unit tests for each class, integration tests for the change-type endpoint
