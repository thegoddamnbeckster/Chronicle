# Trakt & SIMKL Inbound Sync — Design Document

**Date:** 2026-04-20  
**Status:** Approved  
**Scope:** Full inbound sync from Trakt and SIMKL into Chronicle — watch history, ratings, watchlist, cast/crew credits, and stub item creation for unrecognised media.

---

## 1. Goals

- Pull everything a provider has for the authenticated user: watch history, ratings, watchlist, and item metadata including cast/crew.
- Create stub `MediaItem` records for items that don't exist in Chronicle yet, then hand them off to the normal enrichment pipeline.
- Record a `InteractionEvent` for each imported watch event and update `LibraryStatus` accordingly.
- Support both a one-time full import and a recurring delta sync (new events since last run).
- Neither Trakt nor SIMKL is required by Chronicle core or any other plugin — both are fully optional.

---

## 2. Non-Goals (deferred)

- **Outbound sync** — pushing watch status from Chronicle back to Trakt/SIMKL. Separate backlog item.
- **Anime media type** — SIMKL supports anime; adding a first-class `anime` type in Chronicle (with file scanner support and Metadata Assignment entries) is a follow-on item.
- **Letterboxd / other providers** — the interface changes are designed to accommodate future providers.

---

## 3. Architecture

### 3.1 Interface additions (`Chronicle.Plugins`)

`IImportProvider` gains two optional methods with default no-op implementations so existing plugins are unaffected:

```csharp
/// <summary>
/// Returns cast and crew for a specific item the provider knows about.
/// Called after stub creation to populate media_credits.
/// Default: returns an empty list.
/// </summary>
Task<List<ImportedCredit>> GetCreditsAsync(
    string externalId,
    string mediaType,
    CancellationToken ct = default)
    => Task.FromResult(new List<ImportedCredit>());

/// <summary>
/// Returns full item metadata for stub creation when the item is not yet in Chronicle.
/// Default: returns null (stub created with title/year from the watch event only).
/// </summary>
Task<ImportedItemMetadata?> GetItemMetadataAsync(
    string externalId,
    string mediaType,
    CancellationToken ct = default)
    => Task.FromResult<ImportedItemMetadata?>(null);
```

New model records added to `Chronicle.Plugins.Models`:

```csharp
public record ImportedCredit(
    string  PersonName,
    string  Role,              // "Director" | "Writer" | "Actor" | "Composer" | "Producer" …
    string? CharacterName,     // actors only
    int?    BillingOrder,      // 1 = top-billed
    string? ExternalPersonId   // source-specific person ID for future dedup
);

public record ImportedItemMetadata(
    string  Title,
    int?    Year,
    string? Overview,
    string? PosterUrl,
    int?    RuntimeMinutes,
    IReadOnlyDictionary<string, string> AdditionalIds  // e.g. tmdb→"movie:550", imdb→"tt0137523"
);
```

### 3.2 Schema additions (`Chronicle.Data`)

**`media_credits` table** — new EF Core migration:

```sql
CREATE TABLE media_credits (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    media_item_id      INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
    person_name        TEXT    NOT NULL,
    role               TEXT    NOT NULL,
    character_name     TEXT,
    billing_order      INTEGER,
    source             TEXT    NOT NULL,     -- 'trakt' | 'tmdb' | 'simkl' …
    external_person_id TEXT,
    created_at         TEXT    NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_media_credits_item   ON media_credits(media_item_id);
CREATE INDEX idx_media_credits_person ON media_credits(person_name);
```

**Delta sync state** — stored in the existing `app_settings` table; no new table required:

```
key: sync_state.chronicle.plugin.trakt.last_synced_at   value: "2026-04-19T02:00:00Z"
key: sync_state.chronicle.plugin.simkl.last_synced_at  value: "2026-04-19T02:00:00Z"
```

### 3.3 `SyncOrchestrationService` (`Chronicle.Services`)

New service that drives the full sync flow. Both background tasks and the future manual-trigger API call this service.

```
SyncAsync(string pluginId, bool fullSync, CancellationToken ct)
  │
  ├─ 1. Resolve IImportProvider from IPluginRegistry
  │      → throw if not found or not authenticated
  │
  ├─ 2. Read last_synced_at from app_settings
  │      → null when fullSync=true or no prior sync
  │
  ├─ 3. Fetch data (parallel where APIs permit):
  │       GetWatchHistoryAsync(since: lastSyncedAt)
  │       GetRatingsAsync()       ← always full (APIs don't support delta)
  │       GetWatchlistAsync()     ← always full
  │
  ├─ 4. For each imported item → MatchOrCreateAsync():
  │       a. ExternalId lookup in media_external_ids  (source = provider)
  │       b. AdditionalIds lookup in media_external_ids (tmdb, imdb, tvdb …)
  │       c. Title + Year normalised search in media_items
  │       d. No match → GetItemMetadataAsync → CreateStubAsync
  │
  ├─ 5. Upsert all known IDs into media_external_ids
  │      → on ID change: cascade-reset sibling enrichment rows to Pending
  │
  ├─ 6. For each watch event:
  │       INSERT InteractionEvent (skip if same MediaItemId + Timestamp exists)
  │
  ├─ 7. Upsert LibraryStatus per item (see priority rules below)
  │
  ├─ 8. Upsert user rating on library entry (from imported ratings)
  │
  ├─ 9. For newly created/matched stub items:
  │       GetCreditsAsync → upsert media_credits rows (replace all for source)
  │
  ├─ 10. Write last_synced_at = UtcNow to app_settings
  │
  └─ 11. Return SyncSummary { ItemsMatched, StubsCreated, WatchEventsAdded,
                               CreditsAdded, Errors[] }
```

### 3.4 Stub item creation

When `MatchOrCreateAsync` finds no existing item:

1. Call `GetItemMetadataAsync(externalId, mediaType)` — provider returns title, year, overview, poster URL, runtime, and all known IDs.
2. Create `MediaItem` with `HierarchyLevel = 0`, `metadata_json[pluginId] = { raw data }`.
3. Insert all known IDs into `media_external_ids`.
4. Seed `Pending` enrichment rows for **all currently loaded metadata plugins**, pre-populating `ExternalId` on each row where we already have an ID for that plugin (e.g. populate TMDB enrichment row with `movie:550` so TMDB calls `GetByIdAsync` directly instead of searching by title).

**TV episode hierarchy:**

```
ImportedWatchEvent { MediaType: "tv_episode", ShowExternalId, Season, Episode }
  → match/create Show    (HierarchyLevel 0)
  → match/create Season  (HierarchyLevel 1, parent = Show)
  → match/create Episode (HierarchyLevel 2, parent = Season)
  → InteractionEvent recorded against Episode
  → LibraryStatus "Watching" set on Show
```

### 3.5 LibraryStatus priority rules

| Condition | Status applied |
|---|---|
| Watch event imported | `Completed` (movies/single items) / `Watching` (shows) |
| On watchlist, never watched | `PlanToWatch` |
| Has rating but no watch event | `Completed` (implied watched) |
| Chronicle already has a user-set status | **Leave it alone — Chronicle wins** |

The last rule prevents a sync from silently overwriting `Dropped` or `OnHold` statuses the user set deliberately.

### 3.6 Multi-ID handling & cascading refresh

Every ID a provider returns is stored in `media_external_ids`. Trakt returns up to six IDs per item (trakt, slug, imdb, tmdb, tvdb, tvrage); SIMKL returns trakt, tmdb, imdb, and anidb IDs alongside its own.

When any plugin upserts an external ID and the value **changes** (not just confirms the existing value), all sibling enrichment rows for that `MediaItemId` are reset to `Pending` with `RetryCount = 0` and `ExternalId = null`. This ensures:

- A Fix Match in TMDB that corrects the canonical identity triggers Trakt/SIMKL sync rows to re-run against the corrected item.
- A future plugin that identifies an item triggers all other plugins to refresh.

This logic lives in `UpsertExternalIdForEnrichmentAsync` (already exists in `MetadataEnrichmentService`; cascade behaviour is a new addition).

---

## 4. Plugin implementation specifics

### 4.1 Trakt (`Chronicle.Plugin.Trakt`)

Adds to `TraktClient`:

| Method | Trakt API endpoint |
|---|---|
| `GetItemMetadataAsync` | `GET /movies/{id}?extended=full` or `GET /shows/{id}?extended=full` |
| `GetCreditsAsync` | `GET /movies/{id}/people` or `GET /shows/{id}/people` |

Returns full cast (character names, billing order) and crew (directors, writers, producers). Rate limit: 1 000 calls / 5 min — sync service logs a warning if approaching the limit and backs off.

OAuth token refresh is handled by the existing `TraktClient`; new methods run through the same authenticated path.

### 4.2 SIMKL (`Chronicle.Plugin.SIMKL`)

Adds to `SimklClient`:

| Method | SIMKL API endpoint |
|---|---|
| `GetItemMetadataAsync` | `GET /movies/{id}?extended=full` or `GET /shows/{id}?extended=full` |
| `GetCreditsAsync` | No credits endpoint — returns empty list (default) |

When SIMKL returns an empty credits list, TMDB enrichment (triggered by the pre-seeded enrichment row with the TMDB ID) supplies cast/crew instead.

### 4.3 Manifest background tasks (both plugins)

```json
"background_tasks": [
  {
    "task_id":                  "import-all",
    "display_name":             "Import All",
    "description":              "One-time full import of your entire watch history, ratings, and watchlist.",
    "default_cron":             "",
    "default_enabled":          false,
    "schedulable":              false,
    "run_confirmation_title":   "Import everything from [Provider]?",
    "run_confirmation_message": "This pulls your full history and may take several minutes. Existing records will not be duplicated."
  },
  {
    "task_id":         "delta-sync",
    "display_name":    "Delta Sync",
    "description":     "Pulls new watch events, ratings, and watchlist changes since the last sync.",
    "default_cron":    "0 2 * * *",
    "default_enabled": true,
    "schedulable":     true
  }
]
```

Background task execution is handled entirely in the host — `SyncOrchestrationService.SyncAsync(pluginId, fullSync: taskId == "import-all")`. No `IPluginTask` implementation required in the plugin DLLs.

---

## 5. UI surface

| Feature | What happens |
|---|---|
| Enrichment Status box | Trakt and SIMKL appear automatically (both implement `IMetadataProvider`) |
| Background Tasks page | Each plugin gets its own `PluginTaskGroup` fold with Import All + Delta Sync tasks |
| Media detail page | `PluginMetadataBox` renders for items where the plugin has data in `metadata_json` — naturally filtered, no special casing |
| Metadata Assignment | Trakt/SIMKL appear as assignable plugins for the media types they declare in `GetSupportedMediaTypes()` |

---

## 6. Follow-on backlog items

- **Anime media type** — `anime` row in `media_types`, hierarchy config (Show → Season → Episode), file scanner folder type, Metadata Assignment entries, SIMKL declaring anime support.
- **Outbound sync** — pushing watch status from Chronicle to Trakt/SIMKL.
- **Person pages** — `external_person_id` in `media_credits` lays the groundwork for future browse-by-person / filmography views.

---

## 7. Files to create / modify

| File | Action |
|---|---|
| `Chronicle.Plugins/IImportProvider.cs` | Add `GetCreditsAsync` + `GetItemMetadataAsync` default methods; add `ImportedCredit` + `ImportedItemMetadata` records |
| `Chronicle.Core/Models/MediaCredit.cs` | New EF entity |
| `Chronicle.Data/ChronicleDbContext.cs` | Add `DbSet<MediaCredit>` |
| `Chronicle.Data/Migrations/` | New migration: `AddMediaCredits` |
| `Chronicle.Services/SyncOrchestrationService.cs` | New service |
| `Chronicle.Services/ISyncOrchestrationService.cs` | New interface |
| `Chronicle.Services/MetadataEnrichmentService.cs` | Extend `UpsertExternalIdForEnrichmentAsync` with cascade-reset logic |
| `Chronicle.API/Controllers/SyncController.cs` | New controller: manual trigger endpoint |
| `Chronicle.API/Program.cs` | Register `SyncOrchestrationService` |
| `Chronicle.Plugin.Trakt/TraktClient.cs` | Add `GetItemMetadataAsync` + `GetCreditsAsync` |
| `Chronicle.Plugin.Trakt/TraktPlugin.cs` | Override new `IImportProvider` methods |
| `Chronicle.Plugin.Trakt/manifest.json` | Add background tasks |
| `Chronicle.Plugin.SIMKL/SimklClient.cs` | Add `GetItemMetadataAsync` |
| `Chronicle.Plugin.SIMKL/SimklImportProvider.cs` | Override `GetItemMetadataAsync` |
| `Chronicle.Plugin.SIMKL/manifest.json` | Add background tasks |
