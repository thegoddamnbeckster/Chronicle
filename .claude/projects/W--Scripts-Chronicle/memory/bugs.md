# Chronicle — Bug Tracker

## Open Bugs

### BUG-039: MetadataEnrichmentService had two independently-implemented enrichment engines (EnrichOneAsync / EnrichItemCoreAsync)
**Status:** Fixed *(2026-07-13)*
**Symptom:** Discovered while investigating why the Hardcover enrichment for "Endymion" (item 274316) flipped from `Completed` (real cover art, overview, rating) to `Status=NotFound` within ~20 minutes, with a self-contradictory `DiagnosticsJson` (`failureReason: "Matched successfully."` alongside `Status: NotFound`).
**Root cause:** Two ~1,100-line methods independently reimplemented the same conceptual operation — `EnrichOneAsync` (used only by the batch/scheduled path, `EnrichPendingAsync`) and `EnrichItemCoreAsync` (used only by the single-item path — Refresh button, Fix Match, Resync All, and all cascaded children). Neither called the other, and they had drifted:
  - **No coordination lock** — a batch pass and a single-item refresh touching the same `(item, plugin)` row raced with zero synchronization (the only lock, `_pluginLocks`, stops two *batch* runs from overlapping — it does nothing for a single-item call hitting an item a batch pass is also mid-way through). Whichever `SaveChangesAsync` landed last silently won.
  - **Diagnostics captured from a stale `row.Status`** in one path, `failureReason` computed before the final status was set on a later run — exactly the contradictory diagnostics that surfaced this whole investigation.
  - **The batch path had no confidence-threshold or fan-edit title-match guard at all.** `EnrichItemCoreAsync` rejected any search candidate scoring below 50, and separately rejected file-scanner-created root items whose matched title didn't cover ≥60% of the item's own name tokens (the guard that stops "Alien - Darksteel Cut" from being silently identified as "Alien") — but `EnrichOneAsync` (the scheduled/unattended background pass — what actually runs automatically) had **neither check**, and would accept whatever scored highest regardless of confidence. This is the same bug class the very first investigation of this whole session (fan edits merging into official movies) was about, still unguarded on the path that runs unattended.
  - Two separate merge functions (`MergeMetadata` vs `MergeProviderResult`) — only `MergeMetadata` cleared a stale TMDB poster on a child item when a fresh match returned none; the single-item path never did. Only `MergeProviderResult` bumped `item.UpdatedAt`; the batch path never did.
  - Two separate diagnostics JSON shapes — the frontend's `EnrichmentDiagnostics` TypeScript interface (`searchQuery/candidatesReturned/failureReason/topCandidates/scannerSignals`) matched the batch path's shape; the single-item path wrote a different anonymous-type shape missing `failureReason`/`scannerSignals` entirely, and skipped writing diagnostics at all on any ID-reuse success path.
  - Movie-collection re-parenting (`EnsureCollectionParentAsync`/`EnsureCollectionStubsAsync`) only ran from the batch path — a Fix Match or manual Refresh on a movie never triggered collection organization.
  - `CascadeToChildrenAsync`'s recursive call never passed `allProviders` through, so cascaded children never got cross-ref ID seeding.
**Fix:** Consolidated into one canonical `EnrichItemCoreAsync(row, options, ...)` implementation (taking the superset of both paths' capabilities), wrapped in a new per-`(MediaItemId, PluginId)` `SemaphoreSlim` lock so any two callers touching the same row now serialize instead of racing. Every caller (`EnrichPendingAsync`'s batch loop, both `EnrichItemAsync` overloads, `CascadeToChildrenAsync`) now funnels through it; a thin `MediaItem`-taking overload handles load-or-create-row for single-item callers and re-derives `PluginAuthException` from the final row status (since the locked core method itself must never throw — a batch pass has to keep processing the rest of its items after one auth failure). Net effect: `−264` lines despite gaining capability. 4 new unit tests cover the previously-batch-only-missing confidence/title-match guards and a positive-path sanity check; full suite is 327/328 (the 1 failure is the pre-existing unrelated `SyncOrchestrationServiceMatchTests` issue).
**How this was found:** Not a user bug report — surfaced while investigating why a Hardcover match for "Endymion" reverted after the user manually corrected the book's data on hardcover.app, which led to "why did two enrichment runs race" → "why is there no lock" → "why are there two implementations to lock in the first place."

---

### BUG-038: Hardcover plugin's candidate scoring had no data-completeness tiebreaker
**Status:** Fixed *(2026-07-13, Chronicle.Plugin.Hardcover)*
**Symptom:** User reported Hardcover matched "Endymion" to a sparse/incomplete book record (no cover art) despite hardcover.app having a properly-populated entry for the same title; user fixed the data on Hardcover's own site.
**Root cause:** `ScoreBookCandidateDirect`/`ScoreBookCandidate` in `HardcoverMetadataProvider.cs` scored candidates purely on title/year/author text match — no signal for whether a candidate actually has cover art or community ratings. Hardcover can have multiple book rows for the same real-world title (duplicate/sparse community entries); with no completeness tiebreaker, a well-curated entry could lose a tie to an empty duplicate purely on API result order.
**Fix:** Added a small tiebreaker (+5 for cover art present, +2 for ratings present) to both scoring functions — small relative to the title/year/author signals (60+/20/15/20) so it only ever breaks near-ties, never overrides a genuinely better match. Rebuilt and redeployed the plugin DLL.

---

### BUG-037: ABS metadata-provider bridge didn't validate ABS's own mediaType request parameter
**Status:** Fixed *(2026-07-13, Chronicle.Service.MetadataProvider.Audiobookshelf)*
**Symptom:** User asked for confirmation that a single bridge instance could never mix media types across requests. Investigation found the bridge ignored the `mediaType=` query param ABS sends on every `/search` request, always searching whatever Chronicle media type was in its static config regardless of what ABS actually asked for.
**Root cause:** `provider_server.py`'s `do_GET` read `query`/`author` from the querystring but never read `mediaType`. Not an active bug today (the user's single ABS library only ever sends `mediaType=book`, matching the bridge's only configured type), but a real latent risk if the same bridge instance were ever wired up as the Custom Metadata Provider for more than one ABS library (e.g. adding a Podcast library later).
**Fix:** Added `abs_media_type` config setting (default `book`). The bridge now rejects (clean empty-matches response, no Chronicle call at all) any request whose `mediaType` disagrees with the configured value, instead of silently answering for the wrong content type.

---

### BUG-034: Hardcover `book_mappings` schema change — `isbn_13`/`isbn_10` fields removed
**Status:** Fixed *(already resolved before 2026-07-13, tracker was stale — commit 819efc1, "remove broken book_mappings fields")*
**Symptom:** `System.InvalidOperationException: Hardcover GraphQL error: field 'isbn_13' not found in type: 'book_mappings'` thrown during `FetchBookAsync` → `GetByIdAsync`. Affects all queries that request `book_mappings { isbn_13 isbn_10 }`: `GetBookByIdAsync`, `GetBooksByTitleExactAsync`, `GetBooksByTitleAndAuthorAsync`, `GetBookBySlugFullAsync`.
**Root cause:** Hardcover changed their `book_mappings` GraphQL type and removed (or renamed) the `isbn_13` and `isbn_10` fields from the public API.
**2026-07-13 check:** No query in `HardcoverClient.cs` requests `book_mappings` at all anymore — the field requests were removed outright (the "if ISBN data is no longer available, remove the field requests entirely" option this entry itself suggested). `HcBookMapping.Isbn13`/`Isbn10`/`BookMappings` are still declared in `HardcoverModels.cs` but are dead code now (harmless — nothing populates or reads them since nothing requests the field).  

---

### BUG-009: Duplicate cleanup removes valid items (false positives at scale)
**Status:** Fixed *(2026-07-13, commit 981af7f)*
**Symptom:** Running the duplicate cleanup operation eliminates upwards of 30,000 media items from the library. The library does not contain 30,000 duplicates — the vast majority of items being removed are valid, unique entries.  
**Root cause:** `DuplicateCleanupService.ExtractFilePath` preferred `fileScanner.folderPath` over `fileScanner.filePaths[0]` as the duplicate detection key. `folderPath` is the **parent directory** of an item's files (e.g. `/TV/Show/Season 1/`) and is shared by every item in that folder. Every TV episode in a season, every music track in an album, etc. all had the same `folderPath`, so the cleanup grouped them all as duplicates and deleted all but the highest-scored one per folder.  
**Fix:** `ExtractFilePath` now uses `filePaths[0]` only. Individual file paths are globally unique per item; folder paths are not. `folderPath` is explicitly never used as a duplicate key.

---

### BUG-010: Duplicate cleanup misses fanedit/movie cross-type duplicates
**Status:** Fixed *(2026-07-13, commit 981af7f)* — **correction: the original "fix" below was itself the bug**
**Symptom:** Fan edits kept getting silently absorbed into their source movie (or vice versa) by the automated cleanup — reported by the owner across several collections (Alien, Terminator, Star Wars, Waterworld, and more) before this was traced.
**Original (wrong) diagnosis:** Grouping by `filePaths[0]` regardless of media type was described as the *fix* for cross-type duplicate detection ("the DB query already searches across all types — no type filter exists"). This was backwards — it's what let Pass 1 silently merge a Fan Edit into a Movie (or any two differently-typed items) whenever they resolved to the same file-path key, exactly matching Pass 2's own guard comment about why that must never happen.
**Real root cause (found 2026-07-13):** Two incompatible `fileScanner` JSON schemas existed across `FileScanService`'s write paths (a dead singular `"filePath"` key vs. the array `"filePaths"` actually used everywhere) — so file-path matching silently missed existing items and re-created duplicates on rescan, and the flat scan path (`ScanAsync`/`FindExistingItemAsync`) never checked file-path identity at all before falling back to title/external-ID matching. Those upstream matching gaps are what produced the coincidental cross-type filePaths collisions Pass 1 then merged.
**Fix:** Unified the schema via `FileIdentityJson` helpers; `FindExistingItemAsync` now checks exact file-path identity first; `UpsertGroupItemAsync` matches by exact file path before falling back to folder path. Pass 1 now splits file-path groups by `MediaTypeId` and never merges across types (mirrors Pass 2), logging a warning instead when a path collision spans types. 52 historical bad cross-type merges were identified and unmerged; the resulting duplicate stubs (see BUG-035) were then cleaned up.
**Known remaining gap:** `UpsertGroupItemAsync`'s exact-file-path matching *is* type-independent by design (needed so a manual "Change Type" survives a rescan without duplicating) — a scan-matching bug could in theory still mis-attach a rescanned folder to the wrong existing item (this is how item 352711 ended up with a different fan edit's file path on it at one point). The auto-merge consequence is now blocked, but the underlying mis-attachment mechanism itself hasn't been fully root-caused — worth a dedicated look if it recurs.

---

### BUG-035: Unmerge doesn't restore Year/Number, so restored items look like fresh duplicates
**Status:** Fixed *(2026-07-13, commit 981af7f)*
**Symptom:** After unmerging any historical merge, the restored item shows up as a visible duplicate in its collection (blank poster, no year) instead of collapsing back with its sibling — required a second manual cleanup pass every time, e.g. after unmerging the 52 BUG-010 merges, and again after the owner unmerged more in the Terminator collection.
**Root cause:** `MediaItemMerge` (the merge log) never captured `Year`/`Number` at merge time, so `MergeService.UnmergeAsync`'s restored stub always has `Year = null`. Pass 3's duplicate grouping key requires an *exact* year match, so a restored item (`Year = null`) never re-groups with its real sibling (`Year = 1991`, etc.).
**Fix:** Added `LoserYear`/`LoserNumber` to the merge log (migration `20260713082500_AddLoserYearNumberToMergeLog`), captured at merge time, restored on unmerge. Added **Pass 4** to `DuplicateCleanupService`: same parent + same normalized name + same media type is treated as a duplicate regardless of year (safe — a collection's members are already deduped by external ID upstream), with a guard that skips merging when both sides have differing non-null `Number` (protects genuinely distinct same-titled tracks/episodes, e.g. a live + studio version of the same song under one album).

---

### BUG-036: Chronicle.Plugin.TMDB's default branch didn't compile
**Status:** Fixed *(2026-07-13, Chronicle.Plugin.TMDB commit 534a02b, released as v1.3.0)*
**Symptom:** Not user-visible directly — found while auditing this and sibling repos for uncommitted/unpushed work. `dotnet build` on `Chronicle.Plugin.TMDB`'s `master` branch (its actual GitHub default — a stale non-default `main` ref was masking this, see the repo-audit note below) failed with `CS1061: 'TmdbCollectionPart' does not contain a definition for 'VoteAverage'`.
**Root cause:** `TmdbMetadataProvider.cs` read `p.VoteAverage` off `TmdbCollectionPart` (to populate a collection member's `Rating`) but the property was never added to `TmdbModels.cs` — every other `Tmdb*` model already had it, this one was missed. The commit that added the read (925e0d8, `feat(tmdb): capture belongs_to_collection in movie ExtendedData`) landed without the corresponding model change, so `master` has not compiled since.
**Fix:** Added `vote_average` to `TmdbCollectionPart`, matching every other model. Also surfaced two more stale-repo issues while investigating: `Chronicle.Plugin.TMDB` and `Chronicle.Plugin.MusicBrainz` both had a duplicate non-default `main` ref sitting ahead of the real default `master` branch (commits landed on `main` but `master` — what CI/clones/releases actually see — never got them); and no GitHub release had been cut since v1.2.0 despite 16 commits landing (see BUG-018, now fixed with v1.3.0).
**How this class of bug slips through:** No CI on this repo (or any sibling plugin repo) runs a build on push — a broken commit only gets caught by someone building locally, which apparently hadn't happened since 925e0d8 landed.

---

### BUG-013: Metadata Assignment plugin order not persisting across page loads
**Status:** Fixed *(already resolved before 2026-07-13, tracker was stale)*
**Symptom:** After reordering plugins on the Metadata Assignment page and clicking "Save Changes" (which shows "Saved ✓"), the ordering reverts to the default on the next page load as if the save never happened.
**Root cause:** Unknown — needs investigation. Possible causes: the `PUT /settings/metadata-assignment` succeeds but the JSON stored in `app_settings` is not read back correctly on `GET`; or the `assignments` state on load overwrites saved data with defaults for fields not explicitly present in the saved JSON.
**2026-07-13 check:** Both persistence paths have genuine, deliberately-customized data in the live DB right now — `metadata_assignment.config` (per-field plugin priority) and `plugin_display_order.config` (general display order, saved via its own dedicated `PUT /settings/plugin-display-order`, auto-saved immediately on drag-reorder). Neither looks like a default fallback; both are non-trivial custom orderings across several media types. The round-trip works.  
**Fix:** Verify the round-trip: confirm PUT writes the full ordered assignment to `app_settings.value`, and GET deserialises and returns it faithfully without substituting defaults for any field present in the stored JSON.

---

### BUG-014: FanEdit plugin icon not displayed on Metadata Assignment page
**Status:** Open  
**Symptom:** FanEdit plugin rows on the Metadata Assignment page show no icon. TMDB rows correctly display the TMDB colour icon. The FanEdit manifest declares `iconUrl: "https://www.fanedit.org/favicon.ico"`.  
**Root cause:** The icon proxy (`GET /api/v1/plugins/{id}/icon`) previously rejected SVG content, and fanedit.org may serve their favicon as SVG (or the proxy is failing the fetch/magic-byte check for another reason). The SVG→PNG conversion fix was deployed in commit 492d5cc but has not yet been tested against the live fanedit.org favicon.  
**Fix:** Deployed in 492d5cc — restart the API and verify the icon now loads. If it still fails, inspect the proxy response for that plugin's id to determine whether the content-type or magic-byte check is the remaining obstacle.

---

### BUG-011: FileScanner box shows supplemental file paths as raw text, not rendered content
**Status:** Fixed *(2026-07-13 — local poster thumbnail + NFO parsing)*
**Symptom:** When a fan edit (or movie) folder contains a poster image (e.g. `poster.jpg`) or an NFO sidecar file, the FileScanner metadata box on the media detail page shows the file path as plain text rather than rendering the image as a thumbnail or parsing the NFO into readable fields.
**Root cause:** The FileScanner metadata box on `MediaDetailPage.tsx` (a dedicated section, separate from the generic `PluginMetadataBox` component used for plugin data) always rendered `fileScannerMeta.localPosterPath` as plain text.
**2026-07-13 fix (poster):** A secure `GET /api/v1/media/{id}/local-poster` endpoint already existed (path comes only from the item's own DB record, never request input; extension allowlist; existence check) and was already used for the item's main poster elsewhere — the FileScanner box just never used it. Now renders an actual `<img>` thumbnail via that endpoint alongside the path text. Couldn't test against real data — no item in the live library currently has `localPosterPath` populated (scanner hasn't found local poster files in any scanned folder) — verified instead that the endpoint responds correctly (404 for no-poster) and that the frontend reuses the exact URL pattern already proven for regular poster rendering.
**2026-07-13 fix (NFO parsing):** Added a `NfoPath` field to `ScannedFile`/`ScanGroup`/`ScanGroupImport`, threaded through both the flat and grouped scan pipelines (`BuiltInFileScannerPlugin.cs`, `ScanGroupingService.cs`) and the manual scan-preview/import round trip (`ScanGroupDto`/`ImportGroupDto`/`groupToPayload`), plus a new `NfoDetailParser` (`Chronicle.Services/Scan/NfoDetailParser.cs`) that parses the richer display fields (plot, genres, rating, mpaa, studio, runtime, premiered, director, writers, cast, collection name) on demand — kept entirely separate from the existing scan-time-only `NfoSignalExtractor` (matching fields only), which is untouched. New `GET /api/v1/media/{id}/nfo` endpoint mirrors the `/local-poster` endpoint's security pattern (path only from the item's own DB record, must end in `.nfo`, existence check before parsing). `MediaDetailPage.tsx`'s FileScanner box now renders these fields when present, fetched via a new `getNfoDetail` React Query call gated on `fileScannerMeta.nfoPath` being set. 9 new unit tests in `NfoDetailParserTests.cs` using the real `2 Fast 2 Furious (2003).nfo` structure as a fixture.

---

### BUG-012: Diagnostic footer shows [MISSING] for database path
**Status:** Not reproducing *(checked 2026-07-13)* — leaving open, downgraded from "needs a fix" to "watch for recurrence"
**Symptom:** The diagnostic/status footer in the UI shows `[MISSING]` for the database path field instead of the actual path to `chronicle-dev.db` (or the production database).
**Root cause:** Unknown — the value is likely not being passed through to the frontend correctly, or the endpoint that supplies it is returning null/empty for the DB path field.
**2026-07-13 check:** Hit `GET /api/v1/diagnostics` directly against the live (post-restart) server — `dbExists: true`, `dbPath` resolved correctly to the real `chronicle-dev.db`, size matched. `DiagnosticsController.GetDbPath()` already correctly resolves relative `Data Source=` paths against `Environment.CurrentDirectory` (there's an explicit comment there about why, suggesting this was already fixed once). Whatever produced `[MISSING]` isn't happening under the current launch method — may have been specific to a different launcher/working-directory combination (e.g. a script that `cd`s somewhere unexpected before starting the API) that isn't in play right now. If this resurfaces, check `Environment.CurrentDirectory` at the moment `GetDbPath()` runs vs. whatever process/script actually launched the API that time.

---

### BUG-029: Console/log output needs colourisation
**Status:** Fixed *(2026-04-21)*  
**Symptom:** All enrichment log output is the same colour — timestamps, item names, sub-items, and errors are visually indistinguishable in the PowerShell console.  
**Fix:** Switched Serilog console sink from default theme to `AnsiConsoleTheme.Code`. Timestamps are now dark grey, Info lines cyan, Warning yellow, Error/Fatal red. Output template updated to `HH:mm:ss [LVL] Source: Message` for tighter formatting.  
**Note:** Requires API restart to take effect. Needs PowerShell 7+ (`pwsh`) for ANSI support — old `powershell.exe` will show no colours.

---

### BUG-030: Add Media search returns MusicBrainz person results for TV/Movie queries
**Status:** Fixed *(already resolved before 2026-07-13, tracker was stale)*
**Symptom:** Searching "Better Call Saul" on the Add Media page with "TV Shows" selected returns "Peter Gould (Person · Better Call Saul)" — a MusicBrainz artist result — instead of the TV show. All type tabs return the same MusicBrainz-sourced results.
**Root cause (as originally described):** When TMDB is not installed or unhealthy, `SearchMetadataAsync` falls back to the first available metadata provider (MusicBrainz). MusicBrainz doesn't filter by `MediaTypeName` for non-music types, so it returns person/artist results for any query.
**2026-07-13 check:** Neither half of this reproduces anymore. `FileScanService.SearchMetadataAsync` calls `ProvidersForType(mediaTypeHint)`, which filters `_registry.GetMetadataProviders()` down to only providers whose `GetSupportedMediaTypes()` includes the requested type — there's no "fall back to first available provider" path left at all. And `Chronicle.Plugin.MusicBrainz`'s `GetSupportedMediaTypes()` only declares `"music"` — it was never going to be selected for a movie/TV search in the first place. Both sides check out; presumably fixed as part of BUG-027's work (the entry already marked fixed) without the tracker being updated for this one.

---

### BUG-031: SIMKL OAuth polling fails after user authorizes; no retry button
**Status:** Fixed *(2026-07-13, Chronicle.Plugin.Simkl v1.1.1)*
**Symptom:** After clicking the SIMKL PIN link, visiting simkl.com/pin, and authorizing the app, the polling returns "Polling failed — please try again." The poll code is then expired/consumed and there is no "Try Again" / "Get New Code" button to restart the flow.
**Root cause:** `PinPollResponse.Result` was a non-nullable required `string`. Simkl's pending-state poll response doesn't reliably include every field on every call, so a response missing `result` threw a `JsonException` inside `PollPinAsync` that wasn't caught there — it bubbled up into `SimklImportProvider.PollAuthAsync`'s generic `catch (Exception ex)`, which reported it as `Denied` with the raw parse-error message, matching the exact "Polling failed" symptom (and firing even on legitimate pending polls, not just genuine failures).
**Fix:** Made `PinPollResponse.Result` nullable; `PollPinAsync` now catches `JsonException` specifically and treats it as still-pending rather than propagating. Also added the requested "Try Again" button — `ImportPage.tsx`'s auth section now has a distinct error state (separate from the in-progress device-flow state) with a retry button that calls `startAuth` again for a fresh code, for both Simkl and Trakt (shared component).

---

### BUG-032: Trakt Connect Account returns 500 error
**Status:** Fixed *(2026-07-13)*
**Symptom:** Clicking "Connect Account" on the Trakt card in the Import page immediately returns "Request failed with status code 500."
**Root cause:** Confirmed — `TraktPlugin.EnsureConfigured()` and `TraktClient.InitiateDeviceAuthAsync` both correctly throw `InvalidOperationException` (already caught → 400), but a transport-level failure calling Trakt's `/oauth/device/code` (DNS, connection refused, TLS, timeout) throws `HttpRequestException`/`TaskCanceledException` instead, which `ImportController.StartAuth`'s narrow catch block didn't handle — fell through to ASP.NET's default unhandled-exception 500.
**Fix:** Added a `catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)` to both `StartAuth` and `PollAuth` in `ImportController.cs`, returning a proper 4xx with a clear message instead. Same class of bug affects Simkl's poll path too, so `PollAuth` got the same guard.

---

### BUG-033: Music items appearing in TMDB enrichment pending list
**Status:** Fixed *(already resolved before 2026-07-13, tracker was stale)*
**Symptom:** TMDB's Enrichment drill-down page (Pending tab) shows thousands of music tracks (e.g. Prince albums, Roxette tracks) as Pending. TMDB only supports movies and TV; music tracks should never appear there.
**Root cause:** `media_enrichment` rows existed with `plugin_id = 'chronicle.plugin.tmdb'` for music-type media items. These were likely created during the duplicate-plugin-ID corruption era (BUG-025) when MusicBrainz data was incorrectly stored under the `tmdb` plugin ID, or during a re-seed that didn't correctly filter by supported types.
**2026-07-13 check:** Live DB has zero `media_enrichment` rows with `plugin_id='chronicle.plugin.tmdb'` against music-type items — the bad rows are gone. Also verified the code-level safeguard this entry asked for is in place: `PluginService.SeedEnrichmentRowsForProviderAsync` builds its candidate item set directly from `provider.GetSupportedMediaTypes()`, so it can't reseed music rows under TMDB.  
**Fix needed:** (1) DB cleanup: delete `media_enrichment` rows where `plugin_id = 'chronicle.plugin.tmdb'` and the linked `media_item`'s media type is `music`. (2) Code fix: verify `SeedEnrichmentRowsForProviderAsync` correctly filters by `GetSupportedMediaTypes()` so this can't recur.

---

### BUG-028: FanEdit enrichment never finds any matches
**Status:** Fixed *(already resolved before 2026-07-13, tracker was stale)*
**Symptom:** The FanEdit enrichment status always shows 0 completed, even though fan edits are present in the library. Running the enrichment task makes no progress.
**2026-07-13 check:** Live DB shows 19 Completed, 1 NotFound, 7 Pending, zero errors for `chronicle.plugin.fanedit` — spot-checked 3 "Completed" items and confirmed each has real fanedit.org data in its `metadata_json` (not a rubber-stamped completion). `GetSupportedMediaTypes()` correctly declares `"fanedits"`, credentials are configured (encrypted settings present). Whatever caused the original 0% completion rate was already fixed by the time this session found it — `FanEditMetadataProvider.SearchAsync` shows clear signs of iterative fixing (slug-candidate expansion, canonical-URL redirect detection, retry-on-session-expiry) that isn't reflected anywhere in this tracker.

---

### BUG-027: Add Media search not filtered by selected type; Audiobooks missing from type list
**Status:** Fixed *(2026-04-20)*  
**Root cause:** `FileScanService.SearchMetadataAsync` constructed `MediaSearchContext(query)` without passing `MediaTypeName`, so the hint was silently dropped. The frontend mapped type names to a hardcoded `movie/tv/music` hint set.  
**Fix:** `SearchMetadataAsync` now passes `MediaTypeName: mediaTypeHint` to `MediaSearchContext`. Frontend `toMediaTypeHint()` now passes the raw type name (`mediaTypeName.toLowerCase()`) directly so TMDB's `IsMovieType`/`IsTvType` checks work correctly for all types including `fanedits`. Type tabs were already dynamic (from `getMediaTypes()`).

---

### BUG-026: SIMKL/Trakt "Run Now" in Enrichment Status gives "No background task" error
**Status:** Fixed *(already resolved before 2026-07-13, tracker was stale)*
**Symptom:** Clicking "Run Now" for SIMKL or Trakt in the Enrichment Status box produces an alert: *"No background task with ID 'chronicle.plugin.simkl:fetch-missing-metadata' was found."* SIMKL and Trakt are import providers; they have no `fetch-missing-metadata` task.
**Root cause:** `GetEnrichmentStats` returns rows for all plugin IDs present in `media_enrichment`, including import providers. The Enrichment Status UI renders a "Run Now" button for every row and calls `{pluginId}:fetch-missing-metadata`, which doesn't exist for import providers.
**2026-07-13 check:** Resolved via two separate mechanisms. (1) `MetadataEnrichmentService.GetStatsAsync` now builds its plugin list from `registry.GetMetadataProviderEntries()`, which — per an explicit comment there — excludes pure `IImportProvider`s; `TraktPlugin` only implements `IImportProvider` (no metadata-provider component), so it never appears in the Enrichment Status table at all anymore, "Run Now" button included. (2) SIMKL *does* have a metadata-provider component, and its manifest now declares a real `fetch-missing-metadata` background task (confirmed in `background_tasks`: `chronicle.plugin.simkl:fetch-missing-metadata` exists) — so its "Run Now" correctly finds a real task instead of a missing one.  
**Fix needed:** Either filter `GetEnrichmentStats` to metadata providers only, or conditionally hide the "Run Now" button for providers that don't have a `fetch-missing-metadata` background task.

---

### BUG-025: Breaking Bad TMDB metadata box shows MusicBrainz artist ID
**Status:** Fixed *(already resolved before 2026-07-13, tracker was stale)*
**Symptom:** On the Breaking Bad detail page (`/media/621520`), the TMDB metadata section shows `ID: artist:55d920bd-14fb-46f7-8cff-5789a311832b` — a MusicBrainz artist UUID — instead of a valid TMDB TV ID. Fix Match is also broken for this item.
**Root cause:** Likely data corruption from the duplicate-plugin-ID issue (old `"tmdb"` plugin rows mixed with `"chronicle.plugin.tmdb"`); the media_external_ids row for this item under the TMDB plugin contains a MusicBrainz-format ID. Possibly related to BUG-015.
**2026-07-13 check:** Item 621520 no longer exists (id changed at some point, likely through a merge/cleanup). The current Breaking Bad item (340908) has a clean `tmdb: tv:1396` external ID — no MusicBrainz corruption present.

---

### BUG-024: Library page shows "no physical file" icon — should be removed
**Status:** Fixed *(2026-07-13)*
**Symptom:** Library cards show both a "has physical file" (HDD) icon and a "no physical file" (cloud) icon. The cloud icon for items without a local file is noise — the user only wants to see the HDD icon for items that *do* have files.
**Fix:** All three callers (`LibraryPage.tsx` x2, `MediaDetailPage.tsx`) already only rendered `IconHdd` behind `hasPhysicalFile && (...)` — the cloud icon had no remaining callers. Removed the now-dead `IconCloud` export from `FileStatusIcons.tsx`.

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
**Status:** Partially fixed *(2026-04-20; version mismatch resolved 2026-07-13)*
**Symptom:** On the Plugins page the TMDB entry has no icon. Clicking "Configure" shows "Failed to load plugin settings. Please try again." The version shows `1.0.0` but the GitHub release is `1.0.1`.
**Root cause:** `GetSettingsSchema` endpoint returned 404 if the plugin wasn't loaded AND it never checked `ImportProviders` — so Trakt/SIMKL settings also errored.
**Fix (code):** `PluginsController.GetSettingsSchema` now checks `ImportProviders` after `MetadataProviders` and `FileScannerPlugins`.
**2026-07-13:** Manifest version was still `1.0.0` despite three later tagged releases having shipped — bumped to `1.3.0` (matching the new release, see BUG-018) and redeployed the DLL to `plugins/chronicle.plugin.tmdb/`.
**Remaining (user action):** Re-enter TMDB API key in Configure if it's still wiped from the duplicate-ID cleanup (icon still needs checking — untouched this session).

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
**Status:** Fixed *(stale by the time this session found it — v1.2.0 already existed from 2026-04-28; cut v1.3.0 on 2026-07-13 covering the 16 commits since, including the PluginId fix this bug names and a build-breaking bug found the same day)*
**Symptom:** The GitHub repo `thegoddamnbeckster/Chronicle.Plugin.TMDB` shows no release newer than 2026-03-21, despite code changes (PluginId fix, etc.) having been deployed since then.

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
- **BUG-009 — Duplicate cleanup false positives at scale (folderPath used as dup key):** Grouping switched to `filePaths[0]`; `folderPath` never used as a dup key. *(2026-07-13)*
- **BUG-010 — Fan edits silently merged into their source movie (and vice versa):** Root cause was a two-schema `fileScanner` JSON split plus Pass 1 having no media-type guard — unified the schema, added exact-path matching before fallback, split Pass 1 by `MediaTypeId`. 52 historical bad merges identified and unmerged. *(2026-07-13)*
- **BUG-035 — Unmerge doesn't restore Year/Number, restored items look like fresh dupes:** Merge log now carries `LoserYear`/`LoserNumber`; new Pass 4 catches same-parent same-name duplicates regardless of year, guarded against merging distinct same-titled tracks/episodes by differing `Number`. *(2026-07-13)*
- **BUG-036 — Chronicle.Plugin.TMDB's default branch (master) didn't compile:** `TmdbCollectionPart` was missing `VoteAverage`, which `TmdbMetadataProvider.cs` already read. Added the field; released as v1.3.0. *(2026-07-13)*
- **BUG-034 — Hardcover isbn_13/isbn_10 fields removed:** Confirmed already fixed by an earlier untracked commit (819efc1) — the field requests were removed from every query. Tracker was just stale. *(found already-fixed 2026-07-13)*
- **BUG-028 — FanEdit enrichment never finds matches:** Confirmed already fixed — 19 completed with real fanedit.org data verified, credentials configured, search logic shows signs of prior iterative fixing. Tracker was stale. *(found already-fixed 2026-07-13)*
- **BUG-033 — Music items in TMDB enrichment pending list:** Confirmed already fixed — zero offending rows in the live DB, and `SeedEnrichmentRowsForProviderAsync` already filters by `GetSupportedMediaTypes()` so it can't recur. Tracker was stale. *(found already-fixed 2026-07-13)*
- **BUG-025 — Breaking Bad MusicBrainz ID in TMDB slot:** Confirmed already fixed — the corrupted item no longer exists; the current Breaking Bad item has a clean TMDB ID. Tracker was stale. *(found already-fixed 2026-07-13)*
- **BUG-026 — SIMKL/Trakt Run Now gives misleading error:** Confirmed already fixed — Trakt is excluded from the enrichment-stats table entirely (import-only, no metadata-provider component); SIMKL now has a genuinely registered `fetch-missing-metadata` task. Tracker was stale. *(found already-fixed 2026-07-13)*
- **BUG-013 — Metadata Assignment plugin order not persisting:** Confirmed already fixed — both `metadata_assignment.config` and `plugin_display_order.config` have real, deliberately-customized persisted data in the live DB. Tracker was stale. *(found already-fixed 2026-07-13)*
- **BUG-011 (partial) — FileScanner box poster shown as raw text:** Reused the already-existing, already-secure `/api/v1/media/{id}/local-poster` endpoint to render an actual thumbnail instead of just the path string. NFO parsing still outstanding. *(2026-07-13)*
- **BUG-031 — SIMKL OAuth polling fails, no retry button:** `PinPollResponse.Result` made nullable; `PollPinAsync` catches `JsonException` on a malformed pending-state response instead of letting it bubble up as a fake "Denied". Added a "Try Again" retry button to `ImportPage.tsx`. *(2026-07-13, plugin v1.1.1)*
- **BUG-032 — Trakt Connect Account returns 500:** `ImportController.StartAuth`/`PollAuth` now catch `HttpRequestException`/`TaskCanceledException` (transport-level failures) and return a proper 4xx instead of falling through to an unhandled 500. *(2026-07-13)*
