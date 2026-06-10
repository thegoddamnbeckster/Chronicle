# Chronicle — Bug Tracker

## Open Bugs

### BUG-034: Hardcover `book_mappings` schema change — `isbn_13`/`isbn_10` fields removed
**Status:** Open  
**Symptom:** `System.InvalidOperationException: Hardcover GraphQL error: field 'isbn_13' not found in type: 'book_mappings'` thrown during `FetchBookAsync` → `GetByIdAsync`. Affects all queries that request `book_mappings { isbn_13 isbn_10 }`: `GetBookByIdAsync`, `GetBooksByTitleExactAsync`, `GetBooksByTitleAndAuthorAsync`, `GetBookBySlugFullAsync`.  
**Root cause:** Hardcover changed their `book_mappings` GraphQL type and removed (or renamed) the `isbn_13` and `isbn_10` fields from the public API.  
**Fix needed:** Introspect the current `book_mappings` type to find the correct field names, update all four queries in `HardcoverClient.cs`, and update `HcBookMapping` in `HardcoverModels.cs` to match. If ISBN data is no longer available, remove the field requests entirely.  

---

### BUG-009: Duplicate cleanup removes valid items (false positives at scale)
**Status:** Fixed — pending commit  
**Symptom:** Running the duplicate cleanup operation eliminates upwards of 30,000 media items from the library. The library does not contain 30,000 duplicates — the vast majority of items being removed are valid, unique entries.  
**Root cause:** `DuplicateCleanupService.ExtractFilePath` preferred `fileScanner.folderPath` over `fileScanner.filePaths[0]` as the duplicate detection key. `folderPath` is the **parent directory** of an item's files (e.g. `/TV/Show/Season 1/`) and is shared by every item in that folder. Every TV episode in a season, every music track in an album, etc. all had the same `folderPath`, so the cleanup grouped them all as duplicates and deleted all but the highest-scored one per folder.  
**Fix:** `ExtractFilePath` now uses `filePaths[0]` only. Individual file paths are globally unique per item; folder paths are not. `folderPath` is explicitly never used as a duplicate key.

---

### BUG-010: Duplicate cleanup misses fanedit/movie cross-type duplicates
**Status:** Fixed by BUG-009 fix — pending commit  
**Symptom:** Items that exist as both a Movie and a Fan Edit (same file path, same title) are not detected as duplicates by the cleanup operation.  
**Root cause:** The cleanup was grouping by `folderPath` (see BUG-009). A fanedit and a movie in different folders but sharing the same individual file would have different `folderPath` values, so they'd never match.  
**Fix:** Now that grouping uses `filePaths[0]`, two items sharing the exact same physical file path are detected as duplicates regardless of their assigned media type. The DB query already searches across all types — no type filter exists.

---

### BUG-013: Metadata Assignment plugin order not persisting across page loads
**Status:** Open  
**Symptom:** After reordering plugins on the Metadata Assignment page and clicking "Save Changes" (which shows "Saved ✓"), the ordering reverts to the default on the next page load as if the save never happened.  
**Root cause:** Unknown — needs investigation. Possible causes: the `PUT /settings/metadata-assignment` succeeds but the JSON stored in `app_settings` is not read back correctly on `GET`; or the `assignments` state on load overwrites saved data with defaults for fields not explicitly present in the saved JSON.  
**Fix:** Verify the round-trip: confirm PUT writes the full ordered assignment to `app_settings.value`, and GET deserialises and returns it faithfully without substituting defaults for any field present in the stored JSON.

---

### BUG-014: FanEdit plugin icon not displayed on Metadata Assignment page
**Status:** Open  
**Symptom:** FanEdit plugin rows on the Metadata Assignment page show no icon. TMDB rows correctly display the TMDB colour icon. The FanEdit manifest declares `iconUrl: "https://www.fanedit.org/favicon.ico"`.  
**Root cause:** The icon proxy (`GET /api/v1/plugins/{id}/icon`) previously rejected SVG content, and fanedit.org may serve their favicon as SVG (or the proxy is failing the fetch/magic-byte check for another reason). The SVG→PNG conversion fix was deployed in commit 492d5cc but has not yet been tested against the live fanedit.org favicon.  
**Fix:** Deployed in 492d5cc — restart the API and verify the icon now loads. If it still fails, inspect the proxy response for that plugin's id to determine whether the content-type or magic-byte check is the remaining obstacle.

---

### BUG-011: FileScanner box shows supplemental file paths as raw text, not rendered content
**Status:** Open  
**Symptom:** When a fan edit (or movie) folder contains a poster image (e.g. `poster.jpg`) or an NFO sidecar file, the FileScanner metadata box on the media detail page shows the file path as plain text rather than rendering the image as a thumbnail or parsing the NFO into readable fields.  
**Root cause:** The FileScanner plugin stores supplemental file paths in `metadata_json` but the `PluginMetadataBox` component renders all values as generic text — it has no special handling for image paths or NFO content.  
**Fix:** Detect image-extension values in the FileScanner metadata box and render them as `<img>` thumbnails served through the API. Detect `.nfo` values and parse/display the XML content as structured fields.

---

### BUG-012: Diagnostic footer shows [MISSING] for database path
**Status:** Open  
**Symptom:** The diagnostic/status footer in the UI shows `[MISSING]` for the database path field instead of the actual path to `chronicle-dev.db` (or the production database).  
**Root cause:** Unknown — the value is likely not being passed through to the frontend correctly, or the endpoint that supplies it is returning null/empty for the DB path field.

---

### BUG-029: Console/log output needs colourisation
**Status:** Fixed *(2026-04-21)*  
**Symptom:** All enrichment log output is the same colour — timestamps, item names, sub-items, and errors are visually indistinguishable in the PowerShell console.  
**Fix:** Switched Serilog console sink from default theme to `AnsiConsoleTheme.Code`. Timestamps are now dark grey, Info lines cyan, Warning yellow, Error/Fatal red. Output template updated to `HH:mm:ss [LVL] Source: Message` for tighter formatting.  
**Note:** Requires API restart to take effect. Needs PowerShell 7+ (`pwsh`) for ANSI support — old `powershell.exe` will show no colours.

---

### BUG-030: Add Media search returns MusicBrainz person results for TV/Movie queries
**Status:** Open  
**Symptom:** Searching "Better Call Saul" on the Add Media page with "TV Shows" selected returns "Peter Gould (Person · Better Call Saul)" — a MusicBrainz artist result — instead of the TV show. All type tabs return the same MusicBrainz-sourced results.  
**Root cause:** When TMDB is not installed or unhealthy, `SearchMetadataAsync` falls back to the first available metadata provider (MusicBrainz). MusicBrainz doesn't filter by `MediaTypeName` for non-music types, so it returns person/artist results for any query. BUG-027 fixed the type hint being passed; the remaining gap is that a non-music provider should be preferred for movie/TV searches and the result should be filtered/labelled to exclude person/artist entries.  
**Fix needed:** Either (a) require TMDB to be installed for movie/TV searches and show an error if absent, or (b) skip providers that don't declare support for the requested media type in `SearchMetadataAsync`.

---

### BUG-031: SIMKL OAuth polling fails after user authorizes; no retry button
**Status:** Open — being investigated  
**Symptom:** After clicking the SIMKL PIN link, visiting simkl.com/pin, and authorizing the app, the polling returns "Polling failed — please try again." The poll code is then expired/consumed and there is no "Try Again" / "Get New Code" button to restart the flow.  
**Root cause:** Unknown — needs investigation. Likely either: (a) the SIMKL API's token exchange in `PollAuthAsync` throws an unhandled exception after authorization, (b) the poll code expires before the frontend polls (interval timing issue), or (c) the token response is not being handled correctly in the plugin.  
**Fix needed:** (1) Investigate why polling fails after successful authorization. (2) Add a "Try Again" button that calls `startAuth` again to get a fresh PIN.

---

### BUG-032: Trakt Connect Account returns 500 error
**Status:** Open  
**Symptom:** Clicking "Connect Account" on the Trakt card in the Import page immediately returns "Request failed with status code 500."  
**Root cause:** Unknown — the `StartAuthAsync` endpoint only catches `NotSupportedException` and `InvalidOperationException`; any other exception from the Trakt plugin propagates as 500. Likely the Trakt plugin's `StartAuthAsync` throws when the client credentials (client_id, client_secret) are not configured or when the Trakt API call fails.  
**Fix needed:** Investigate what the Trakt plugin throws on `StartAuthAsync`. Catch the specific exception and return a meaningful 4xx error. Ensure the user gets a clear "configure your client credentials first" message if that's the root cause.

---

### BUG-033: Music items appearing in TMDB enrichment pending list
**Status:** Open  
**Symptom:** TMDB's Enrichment drill-down page (Pending tab) shows thousands of music tracks (e.g. Prince albums, Roxette tracks) as Pending. TMDB only supports movies and TV; music tracks should never appear there.  
**Root cause:** `media_enrichment` rows exist with `plugin_id = 'chronicle.plugin.tmdb'` for music-type media items. These were likely created during the duplicate-plugin-ID corruption era (BUG-025) when MusicBrainz data was incorrectly stored under the `tmdb` plugin ID, or during a re-seed that didn't correctly filter by supported types.  
**Fix needed:** (1) DB cleanup: delete `media_enrichment` rows where `plugin_id = 'chronicle.plugin.tmdb'` and the linked `media_item`'s media type is `music`. (2) Code fix: verify `SeedEnrichmentRowsForProviderAsync` correctly filters by `GetSupportedMediaTypes()` so this can't recur.

---

### BUG-028: FanEdit enrichment never finds any matches
**Status:** Open — needs joint investigation  
**Symptom:** The FanEdit enrichment status always shows 0 completed, even though fan edits are present in the library. Running the enrichment task makes no progress.  
**Root cause:** Unknown — needs investigation to determine whether: (a) the FanEdit plugin `GetSupportedMediaTypes()` doesn't include the correct type slug, (b) enrichment rows for fanedit items are not being seeded, or (c) the search/match logic in the FanEdit plugin is failing silently.

---

### BUG-027: Add Media search not filtered by selected type; Audiobooks missing from type list
**Status:** Fixed *(2026-04-20)*  
**Root cause:** `FileScanService.SearchMetadataAsync` constructed `MediaSearchContext(query)` without passing `MediaTypeName`, so the hint was silently dropped. The frontend mapped type names to a hardcoded `movie/tv/music` hint set.  
**Fix:** `SearchMetadataAsync` now passes `MediaTypeName: mediaTypeHint` to `MediaSearchContext`. Frontend `toMediaTypeHint()` now passes the raw type name (`mediaTypeName.toLowerCase()`) directly so TMDB's `IsMovieType`/`IsTvType` checks work correctly for all types including `fanedits`. Type tabs were already dynamic (from `getMediaTypes()`).

---

### BUG-026: SIMKL/Trakt "Run Now" in Enrichment Status gives "No background task" error
**Status:** Open  
**Symptom:** Clicking "Run Now" for SIMKL or Trakt in the Enrichment Status box produces an alert: *"No background task with ID 'chronicle.plugin.simkl:fetch-missing-metadata' was found."* SIMKL and Trakt are import providers; they have no `fetch-missing-metadata` task.  
**Root cause:** `GetEnrichmentStats` returns rows for all plugin IDs present in `media_enrichment`, including import providers. The Enrichment Status UI renders a "Run Now" button for every row and calls `{pluginId}:fetch-missing-metadata`, which doesn't exist for import providers.  
**Fix needed:** Either filter `GetEnrichmentStats` to metadata providers only, or conditionally hide the "Run Now" button for providers that don't have a `fetch-missing-metadata` background task.

---

### BUG-025: Breaking Bad TMDB metadata box shows MusicBrainz artist ID
**Status:** Open  
**Symptom:** On the Breaking Bad detail page (`/media/621520`), the TMDB metadata section shows `ID: artist:55d920bd-14fb-46f7-8cff-5789a311832b` — a MusicBrainz artist UUID — instead of a valid TMDB TV ID. Fix Match is also broken for this item.  
**Root cause:** Likely data corruption from the duplicate-plugin-ID issue (old `"tmdb"` plugin rows mixed with `"chronicle.plugin.tmdb"`); the media_external_ids row for this item under the TMDB plugin contains a MusicBrainz-format ID. Possibly related to BUG-015.  
**Fix needed:** Inspect `media_external_ids` for media_item 621520. Delete the corrupt TMDB external ID row and re-run TMDB enrichment for this item.

---

### BUG-024: Library page shows "no physical file" icon — should be removed
**Status:** Open  
**Symptom:** Library cards show both a "has physical file" (HDD) icon and a "no physical file" (cloud) icon. The cloud icon for items without a local file is noise — the user only wants to see the HDD icon for items that *do* have files.  
**Fix needed:** Remove the cloud/metadata-only icon from `FileStatusIcons.tsx` and all callers. Show only the HDD icon, only when `hasPhysicalFile` is true.

---

### BUG-023: Media list shows 0 item count; no way to toggle ordered/unordered
**Status:** Fixed *(2026-04-20)*  
**Root cause (count):** `MediaListService.GetAllForUserAsync` was missing `.Include(l => l.Items)`, so `l.Items.Count` was always 0 for the summary endpoint.  
**Fix:** Added `.Include(l => l.Items)` to `GetAllForUserAsync`. Badge in `ListDetailPage.tsx` changed from a `<span>` to a `<button>` that calls `updateList(listId, { isOrdered: !list.isOrdered })` on click, with hover style.

---

### BUG-022: Import page duplicates functionality that belongs in Background Tasks
**Status:** Open (design issue)  
**Symptom:** `/import` is a standalone page with "Connect Account" flows for Trakt and SIMKL. The actual import/sync work is triggered as background tasks. Having a separate Import tab feels like a duplicate of the Background Tasks page.  
**Fix needed:** Consolidate — the Connect Account auth flow could live in plugin Settings; the import trigger could live in Background Tasks. Discuss before acting.

---

### BUG-021: TMDB plugin missing icon, unconfigurable, wrong version in UI
**Status:** Partially fixed *(2026-04-20)*  
**Symptom:** On the Plugins page the TMDB entry has no icon. Clicking "Configure" shows "Failed to load plugin settings. Please try again." The version shows `1.0.0` but the GitHub release is `1.0.1`.  
**Root cause:** `GetSettingsSchema` endpoint returned 404 if the plugin wasn't loaded AND it never checked `ImportProviders` — so Trakt/SIMKL settings also errored.  
**Fix (code):** `PluginsController.GetSettingsSchema` now checks `ImportProviders` after `MetadataProviders` and `FileScannerPlugins`.  
**Remaining (user action):** (1) Re-enter TMDB API key in Configure (was wiped during duplicate-ID cleanup). (2) Rebuild/redeploy TMDB plugin DLL from latest source to get `1.0.1`.

---

### BUG-020: Audiobooks not available as media type in File Scan
**Status:** Partially fixed *(2026-04-20)*  
**Root cause:** `GetStatusAsync` only returned media types declared by the scanner's `GetSupportedMediaTypes()` (hardcoded: movies/tv/music). Any type not in that list was hidden from the dropdown.  
**Fix (code):** `GetStatusAsync` now queries all `media_types` from the DB dynamically. `ScanAsync` now falls back to the first available scanner when no scanner explicitly declares the requested type.  
**Remaining:** An "Audiobooks" row must exist in the `media_types` table. Add it via the admin UI or a migration. MusicBrainz plugin's `GetSupportedMediaTypes()` should also declare `"audiobooks"` for enrichment to work.

---

### BUG-019: FanEdit icon missing in Background Tasks page
**Status:** Open  
**Symptom:** The FanEdit plugin group header on the Background Tasks page shows no icon. Other plugins (SIMKL, Trakt) display their icons correctly.  
**Root cause:** Likely the same icon proxy issue as BUG-014 (fanedit.org favicon). The SVG-fix deployed in 492d5cc may not have been tested against the live fanedit.org URL, or the deployed `chronicle.plugin.fanedit` directory still has the pre-fix binary.  
**Fix needed:** Confirm the fanedit.org favicon is reachable via the icon proxy and renders in the UI. Redeploy if necessary.

---

### BUG-018: TMDB GitHub repo has no release since 2026-03-21
**Status:** Open  
**Symptom:** The GitHub repo `thegoddamnbeckster/Chronicle.Plugin.TMDB` shows no release newer than 2026-03-21, despite code changes (PluginId fix, etc.) having been deployed since then.  
**Fix needed:** Tag and publish a new GitHub release (at minimum `v1.0.1`) from the current state of the TMDB plugin after the PluginId fix is committed.

---

### BUG-015: TMDB missing from Enrichment Status box; SIMKL/Trakt non-functional; Trakt health check failing
**Status:** Fixed (TMDB) — SIMKL/Trakt by-design; Trakt health check open  
**Symptom:** TMDB does not appear in the Enrichment Status table on the Background Tasks page. SIMKL and Trakt plugins are installed but appear to do nothing. The Trakt plugin reports unhealthy despite a valid API secret key being configured.  
**Root cause (investigated):**  
- TMDB not in Enrichment Status: Two issues combined — (1) TMDB was uninstalled (see BUG-017). (2) Even after reinstall, `PluginId` in `TmdbMetadataProvider.cs` was `"tmdb"` while the catalog, enrichment rows, and database records all used `"chronicle.plugin.tmdb"`. The mismatch meant enrichment seeding wrote rows under `"tmdb"` but GetStatsAsync looked for `"chronicle.plugin.tmdb"`.  
- SIMKL/Trakt do nothing: These are **import providers** (scrobbling receivers), not metadata enrichment providers. They appear in the plugin list but not in the Enrichment Status table (which is metadata-only by design). Outbound watch-status sync to Trakt/SIMKL is a planned feature (see backlog).  
- Trakt health check failing: Trakt uses OAuth tokens, not simple API keys. The health check calls the Trakt API; if the token format is wrong or expired the check fails.  
**Fix:** `TmdbMetadataProvider.PluginId` changed from `"tmdb"` to `"chronicle.plugin.tmdb"` to match the catalog and all DB records. Source manifest `plugin_id` updated likewise. DLL rebuilt and redeployed. *(2026-04-20)*

---


## Resolved Bugs

- **BUG-001 — TMDB matches TV shows instead of movies:** Added `MediaTypeName` to `MediaSearchContext`; TMDB now restricts to movie or TV endpoint based on item type. *(2026-04-17)*
- **BUG-002 — TMDB enrichment pending count never reaches zero:** Phase 2 of startup seeding no longer resets `NotFound` rows (valid terminal state); "fanedits" added to TMDB supported types. *(2026-04-17)*
- **BUG-003 — FanEdit plugin shows all zeros in enrichment stats:** `ChangeTypeAsync` now creates `Pending` enrichment rows for all plugins supporting the new type after a type change. Phase 3 startup seeding also backfills missing rows. *(2026-04-18)*
- **BUG-004 — Fan edit items re-added to Movies by file scanner:** Removed `mediaTypeId` filter from `FindItemByFilePathAsync` — file paths are globally unique across all types. *(2026-04-17)*
- **BUG-005 — "↑ Library" after type change scrolls to wrong section:** `onMutate` now captures the adjacent library item's element ID before the mutation fires; "↑ Library" navigates to that anchor so the user's scroll position is preserved. *(2026-04-17)*
- **BUG-006 — Change Type modal has transparent background:** `.changeTypeStrip` uses `background: var(--bg-primary)` (always-opaque); `.changeTypeSelect` uses explicit color. *(2026-04-17)*
- **BUG-007 — TMDB metadata box hidden on Fan Edit detail pages:** "fanedits" added to TMDB's `GetSupportedMediaTypes()`. *(2026-04-17)*
- **BUG-008 — Metadata Assignment page missing media types:** Page now shows all active media types; compound keys (`tv.1`, `tv.2`, `music.1`, `music.2`) added for sub-level hierarchy; display names derived from DB `HierarchyLabels`. *(2026-04-18)*
- **Duplicate movies (colon vs dash titles):** Fixed via `FindByTitleAsync` helper with `" - "` → `": "` fallback. *(2026-04-19)*
- **"View in Library" wrong destination:** Fixed to navigate to `/library#media-{id}`. *(2026-04-19)*
- **Fan edits missing from Metadata Assignment:** Fixed by making `AssignableFields` fully dynamic from DB `MediaTypes` table. *(2026-04-17)*
- **Metadata Assignment shows wrong plugins per type (MusicBrainz for Movies):** `availablePlugins` is now a per-type map built by calling each plugin's `GetSupportedMediaTypes()`. *(2026-04-19)*
- **BUG-016 — Fix Match doesn't update media_external_ids:** `UpsertExternalIdForEnrichmentAsync` called from `EnrichOneAsync` after match; replaces old TMDB ID. *(2026-04-19)*
- **BUG-017 — TMDB plugin 500 error on reinstall:** Cleared stale SHA-256, added `UnloadPlugin(0)` safety call, `UninstallPluginAsync` cleans up BackgroundTask rows, catch-all error handling in install endpoints. *(2026-04-19)*
- **BUG-015 — TMDB missing from Enrichment Status (PluginId mismatch):** `TmdbMetadataProvider.PluginId` changed from `"tmdb"` to `"chronicle.plugin.tmdb"`; manifest updated; DLL rebuilt. *(2026-04-20)*
- **Duplicate TMDB plugin entries (two background task groups):** `chronicle.plugin.tmdb/manifest.json` had stale `plugin_id: "tmdb"`; stale `plugins/tmdb/` directory remained alongside the renamed `plugins/chronicle.plugin.tmdb/` dir. Fixed: corrected manifest, deleted old directory, migrated 5901 enrichment rows, removed stale plugin + task DB rows, updated DLL path. *(2026-04-21)*
- **Trakt OAuth tokens not persisted after device auth:** `MergeAndPersistSettingsAsync` in `ImportController` called `JsonSerializer.Deserialize` directly on the encrypted `SettingsJson` (`ENC:...`), throwing a `JsonException` that silently aborted the token save. Fixed: moved merge logic into `IPluginService.MergeSettingsAsync` which uses `_protector.Unprotect()` before deserialising. *(2026-04-21)*
- **Scanner performance:** Fixed v1.2.0. *(resolved)*
- **FanEdit icon not rendering (SVG rejection):** Icon proxy now accepts SVG and rasterises it to PNG server-side via Svg.Skia before caching. *(2026-04-18)*
- **RunTestEnvironment.ps1 DLL locking:** Kill block moved before build/copy loop so API releases file locks before plugin DLLs are overwritten. *(2026-04-18)*
