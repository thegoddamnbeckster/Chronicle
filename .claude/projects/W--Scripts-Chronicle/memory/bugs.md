# Chronicle — Bug Tracker

## Open Bugs

### BUG-049: Uninstalling a plugin didn't stop it reappearing after a restart
**Status:** Fixed *(2026-08-22, Chronicle server)*
**Symptom:** User uninstalled Trakt via the UI; it showed back up (enabled) on the next dev-environment restart. User: "The plugin was uninstalled... If the plugin is uninstalled, it is expected that it is unloaded and becomes unloaded." Correctly called out an earlier wrong assumption on my part.
**Root cause:** `PluginService.UninstallPluginAsync` removed the DB row and unloaded the plugin from the in-memory registry, but never deleted its deployed `plugins/{id}/` folder. `PluginHostService.AutoRegisterBundledPluginsAsync` (exists so bundled plugins like TMDB are available on a fresh install) scans that folder on every API startup and auto-registers any manifest.json it finds with no matching DB row -- it can't tell "never installed" apart from "user explicitly uninstalled this", so the very next restart silently reinstalled Trakt right back.
**Fix:** `UninstallPluginAsync` now deletes the plugin's directory after removing the DB rows (forces the AssemblyLoadContext unload to complete via `GC.Collect()`/`WaitForPendingFinalizers()` first, retries the delete a few times, and is best-effort -- a lingering file lock never fails the uninstall itself). 2 new tests.

---

### BUG-048: TV episodes stop scanning in after switching Kodi's scraper away from Chronicle and back
**Status:** Fixed *(2026-08-22, Chronicle_Scraper v2.13.7)*
**Symptom:** User: "when I switch from the Chronicle Scraper to something else and then back to the Chronicle scraper, Kodi completely dumps the entire library... Kodi has to scan files in. This is what is failing." Confirmed live: Kodi's own log showed, for every TV episode, "Asked to lookup episode ... online, but we have either no episode guide or we are using the local scraper" -- `tvshow_scraper.py` was never being invoked at all (zero matching log lines anywhere).
**Root cause:** Kodi always prefers a local `tvshow.nfo` already sitting in a show's folder over calling the scraper live. `tv_nfo_writer.py`'s `sync_show_nfo()` never wrote an `<episodeguide>` tag into that file -- `get_details()` correctly called `vtag.setEpisodeGuide()` on every *live* scrape, but that value only ever reached Kodi's in-memory VideoInfoTag, never the NFO on disk. So once write_nfo had written a show's NFO once, every later scan that read it directly (not just after a scraper switch -- any routine rescan of an already-scraped show) had no way to learn how to fetch that show's episodes from Chronicle again.
**Fix:** `_build_show_nfo()`/`sync_show_nfo()` now accept the same lookup string `get_details()` passes to `setEpisodeGuide()`, so both paths resolve identically. Existing shows: Settings → "Rebuild local NFOs from Chronicle" regenerates `tvshow.nfo` with the fix without waiting for a natural rescrape. 4 new tests.

---

### BUG-047: Plugin catalog missing most plugins
**Status:** Fixed *(2026-08-22)*
**Symptom:** User: "we're missing several items in the plugin catalog." The catalog (Plugins page → "+ Install Plugin") showed only TMDB, MusicBrainz, and File Scanner, all marked Installed -- FanEdit, Fanart.tv, Hardcover, Movies Remastered (MRDb), SIMKL, TheTVDB, Trakt, TVMaze, and Default Themes were all missing despite being real, already-installed, working plugins.
**Root cause:** `PluginsController.PluginCatalog` is a hand-maintained static C# array (this is exactly the "no hardcoding" pattern this project's own conventions warn against) that was never updated as more plugins shipped -- it only ever had 3 entries.
**Fix:** Added the 9 missing entries (name/description/author/icon/GitHub repo/tags/version, sourced from each plugin's own manifest.json). `Sha256` left empty for all 9, matching TMDB's own existing "cleared — recalculate after each plugin release" precedent -- computing a real digest would need the actual packaged release ZIP, not the source tree. 2 new integration tests (`PluginCatalogTests.cs`): one asserts every currently-known plugin id is present (a tripwire against this exact staleness recurring), one asserts every entry has the fields the install flow actually needs populated.
**Not fixed, flagged for later:** this is still a hardcoded list that will go stale again the next time a plugin ships -- nothing enforces keeping it in sync short of remembering to update it (and the new test) together. A DB-backed or auto-discovered catalog would remove the recurring-staleness risk entirely; not attempted here since it's a larger design change than "restore the missing entries."

---

### BUG-046: Watch History shows ~20 episodes all with the exact same timestamp
**Status:** Fixed *(2026-08-22)*
**Symptom:** User: "No, nobody watched 30 different videos at exactly the same time on the 20th. This makes no sense." -- the Watch History page showed a run of episodes (S28E02 through S28E21) all timestamped "Aug 20, 2026, 9:04 PM", device "—".
**Root cause:** Not a bug in the sense of wrong data -- SIMKL (the source, confirmed actively delta-syncing) only records one `LastWatchedAt` for a show bulk-marked "completed" via its own UI, not a timestamp per episode. `SimklImportProvider.GetWatchHistoryAsync` already correctly falls back per-episode (`ep.WatchedAt ?? showWatchedAt`), but `WatchedAtIsApproximate` was computed as `realEpWatchedAt is null && realShowWatchedAt is null` -- since the show DID have a real date, every episode borrowing it was marked "not approximate", so nothing downstream could tell "the show's one shared date" apart from "this episode's own genuine watch time". The identical Aug 20 9:04 PM timestamp itself is real SIMKL data, correctly propagated -- just presented with no indication it's shared/approximate.
**Fix:**
- `Chronicle.Plugin.Simkl`: `WatchedAtIsApproximate` now `true` whenever `realEpWatchedAt is null` (borrowing the show's date is still an approximation for that specific episode), for both TV and anime.
- `Chronicle` server: new `InteractionEvent.TimestampIsApproximate` column (migration `20260822212432_AddTimestampIsApproximateToInteractionEvent`), set from `evt.WatchedAtIsApproximate` in `SyncOrchestrationService.UpsertWatchEventAsync`, threaded through `HistoryItemDto`/`ScrobbleController.GetHistory`.
- `HistoryPage.tsx`: rows with an approximate timestamp now show a "~" marker (tooltip explains why) instead of presenting the borrowed date as exact. Also fixed an adjacent, already-declared-but-never-wired gap in the same component: `ancestors` (e.g. show/season context) was already returned by the API with a comment saying it exists so "S28E11"-style bare episode codes aren't meaningless, but the page never rendered it -- now shown as "Show › Season ›" before the episode name.
- 2 new tests in `SyncOrchestrationServiceTests.cs` covering both the approximate and exact-timestamp paths end-to-end through `SyncAsync`.

---

### BUG-041: Chronicle_Scraper addon can't connect to Chronicle right after fresh install -- works after a Kodi restart
**Status:** Fixed *(2026-08-22, Chronicle_Scraper v2.13.6, hypothesis 1 addressed; hypothesis 2 not ruled out but considered unlikely)*
**Symptom:** Right after installing the addon, using Settings → "Connect to Chronicle" (the QR/PIN device-auth flow, `lib/device_auth.py`'s `DeviceAuthManager.run()`) fails to connect. Turning Kodi off and back on, then retrying the exact same flow, works.
**Root cause (hypothesis 1, addressed without a live repro):** `DeviceAuthManager._initiate()` read `ADDON.getSetting('chronicle_url')` fresh at call time -- but "Connect to Chronicle" is a `RunScript` action fired from the same Settings screen as the `chronicle_url` text field, and Kodi doesn't guarantee that field's just-typed edit is committed to the addon's settings store before `RunScript` launches this as a separate process. A Kodi restart sidesteps it because the previous session's settings write has already fully flushed by the next launch.
**Fix:** New `_read_chronicle_url()` retries the setting read up to 3 times (0.5s apart) before concluding it's genuinely empty, absorbing the commit race instead of failing on the first read. `run()` also now shows a distinct "Chronicle server URL is not set" message (string #32108) when the URL never settles, instead of misreporting it as "could not contact Chronicle" -- makes a real not-configured case immediately distinguishable from a genuine network/server failure. 5 new tests in `tests/test_device_auth.py`.
**Hypothesis 2 (multi-extension-point registration lag) left unconfirmed:** still no evidence found that the connect flow depends on anything `service.py` sets up; not pursued further since hypothesis 1 is a real, confirmed-by-code-reading race with an unconditionally-safe fix, and the fix should resolve the reported symptom regardless of which hypothesis was the actual cause. Reopen and pull a real `kodi.log` if this recurs after v2.13.6.

---

### BUG-042: Kodi on Shield periodically reports movie collections folder "not reachable"
**Status:** Fixed *(2026-08-22, Chronicle_Scraper v2.13.6)*
**Symptom:** Reported by the user: "in Kodi on the shield, the scraper will often throw up messages saying that it is not able to access the collection folder registered in Kodi." Matches `lib/collection_sync.py`'s `_notify_unreachable` notification verbatim ("Movie collections folder not reachable: {folder}"), throttled to at most one popup per 10 minutes but still firing periodically.
**Root cause:** `sync_collection_art` treated a single failed `xbmcvfs.mkdirs()` (creating a missing set folder) or a single `write_remote_file` `'write_failed'` result as proof the configured network folder was genuinely unreachable. Android's Kodi SMB/network VFS client routinely drops and reconnects its session (Wi-Fi doze, the NAS still waking from its own sleep, a brief DHCP/DNS blip) -- a hiccup that normally clears within a second or two, but a single-shot check reported every one of those as a permanently broken folder.
**Fix:** New `_mkdirs_with_retry`/`_write_with_retry` helpers retry the specific failing operation up to 3 times (1.5s apart) before falling through to `_notify_unreachable`. `write_with_retry` only retries on `'write_failed'` -- a `'download_failed'` says nothing about the destination folder's own reachability, so that outcome still returns immediately. 6 new tests in `tests/test_collection_sync_folder_retry.py`.

---

### BUG-043: Chronicle web settings inputs showed a saved value only as placeholder text; Save could silently wipe it
**Status:** Fixed *(2026-08-22, Chronicle server)*
**Symptom:** User: "I have set this once already. Did it not save? If it's been set, it needs to be editable text." — screenshot showed the already-configured "Collection Folder Path" setting rendered in dim placeholder styling, indistinguishable from unset.
**Root cause:** `LibrarySettingsPage.tsx`'s Collection Folder Path and Batch Size inputs were controlled (`value={...Input}`) but the local input state was permanently initialised to `''` and never synced from the loaded `appSettings` value -- the actual saved value was shown only via the `placeholder` prop, which always renders as dim/ghost text. Clicking Save without editing anything then submitted the empty string, silently clearing an already-configured setting (concretely destructive for Collection Folder Path, whose blank value means "disabled").
**Fix:** Both inputs now re-sync from `appSettings` via a `useEffect` whenever it loads or changes (including right after a save, once the query invalidates), so a saved value always shows as real, editable text. Save is now disabled until the input actually differs from the saved value. Same disabled-until-changed treatment applied to three other places with the same already-set-value-edit shape that were displaying correctly but let a no-op Save go through anyway: `PluginsPage.tsx`'s plugin config panel, `ProfilePage.tsx`, and `UsersPage.tsx`'s user-detail edit form.
**Related fix (found while committing the above):** `.gitignore`'s `plugins/` rule was unanchored, so it also matched `src/Chronicle.Web/src/pages/plugins/` (a real, already-tracked frontend source directory) -- `git add` of any new file created there was being silently refused. Anchored to `/plugins/` (repo root only, matching the intent already stated in the adjacent `src/Chronicle.API/plugins/` rule). Anchoring the pattern also revealed a `docs/plugins/` directory of untracked markdown files that had been invisible to `git status` this whole time -- left as-is (not committed), flagging for the user to review since some look like superseded drafts (`PLUGIN_FANART.md` alongside `PLUGIN_FANART_V3.md`/`PLUGIN_FANART_v2.md`, same pattern for SIMKL/Trakt/Wikipedia).

---

### BUG-044: Library page card poster doesn't update after pinning a new image on the detail/collection screen
**Status:** Fixed *(2026-08-22, Chronicle server)*
**Symptom:** User: "Have you got a bug in your list about the library collection poster not being updated after I update it in the movie collection detail screen?" -- pinning a new poster (or other image) on a media item's or collection's detail page updates that page correctly but the Library page's card grid keeps showing the old image.
**Root cause:** `MediaDetailPage.tsx`'s three image-override mutations (`overrideSetMut`, `overrideClearMut`, `clearAllOverridesMut`) only wrote the updated item into the `['media', mediaId]` React Query cache via `qc.setQueryData` -- unlike every other mutation in the same file (delete, refresh, reparent, merge, change-type, etc.), none of them invalidated the `['library']` query the Library page's cards are built from, so the old cached poster kept showing there until something unrelated happened to invalidate it.
**Fix:** Added `qc.invalidateQueries({ queryKey: ['library'] })` to all three mutations' `onSuccess`, matching the pattern every other mutation in the file already uses.

---

### BUG-045 (feature request): Manually assign an arbitrary image URL when Chronicle has no candidates at all
**Status:** Fixed *(2026-08-22, picked up per "please look after the entirety of manually adding an image")*
**Symptom:** Item 432609, "In This Moment - Rock on the Range 2015" (a concert film), has no automatically-detected images from any provider (Fanart.tv/SIMKL/Trakt/TMDB all show "No match found") -- the existing pin system only lets you choose from images Chronicle already knows about, surfaced via the full-size image viewers, so there was nothing to click.
**Security finding made while scoping this (fixed as a prerequisite):** `MediaController.SetOverride`/`SetOverrideAsync` accepted any string as a canonical-field value with zero URL validation, and the separate `MediaController.PosterProxy` endpoint (`[AllowAnonymous]`) does a server-side fetch of any caller-supplied URL with only a scheme check -- classic SSRF (could be pointed at an internal service or a cloud metadata endpoint, e.g. 169.254.169.254, and have the response streamed back). A user-facing free-text URL input would have made this trivial to trigger. New `Chronicle.Core.Helpers.ExternalUrlSafety` (well-formed http/https check + DNS-resolve-then-reject-private/loopback/link-local/CGNAT ranges, IPv4 and IPv6) is now applied at both choke points -- `SetOverride` only for the 8 actual image-URL canonical fields (title/overview/genres/etc. still accept a plain arbitrary value through the same generic endpoint, unchanged) and `PosterProxy` for every request regardless of source.
**Fix:**
- New `ManualImageUrlModal.tsx` component: paste a URL, live `<img>` preview confirms it actually loads (client-side UX only -- the server independently re-validates and is the real security boundary), then reuses the existing `ImageSlotControls` to pin it into any of the 8 slots -- identical mechanism to picking an existing candidate, just with a manually-supplied source instead of a plugin-supplied one.
- New "+ Add Image URL" button in `MediaDetailPage.tsx`'s top toolbar (`deleteArea`, admin-gated, matching this project's own established convention that entity-level actions live in the toolbar, not nested in a display sub-component -- see `feedback_chronicle_ui_action_placement` memory), available for every item, not just ones with zero candidates.
- 22 new unit tests (`ExternalUrlSafetyTests.cs`) and 10 new integration tests (`MediaOverrideUrlSafetyTests.cs`) covering the validator directly and both hardened endpoints end-to-end (rejects private/loopback/link-local/malformed, accepts a real public URL, confirms non-image fields are still unvalidated).
**Not fully live-verified:** confirmed the exact item (432609) has no images and reproduces the reported empty state; confirmed the backend validation end-to-end via the passing integration tests; could not click through the actual modal as the item's own real admin account (only had a non-admin test account available) -- the button is a straightforward `isAdmin &&`-gated JSX addition following the exact pattern of every other admin-only toolbar button already in the file, so this is considered low-risk, but worth a quick manual click-through.

---

### BUG-040: Global search results can't be scrolled on mobile -- any touch navigates immediately
**Status:** Open *(reported 2026-08-22)*
**Symptom:** On mobile, pressing/dragging on the search results dropdown to scroll it instead immediately navigates to whatever result is under the finger.
**Likely root cause:** `src/Chronicle.Web/src/components/layout/GlobalSearch.tsx:106` -- each result row fires `handleSelect` on `onPointerDown` (not `onClick`), with `e.preventDefault()` called immediately:
```tsx
onPointerDown={e => { e.preventDefault(); handleSelect(item) }}
```
This is almost certainly there to beat a race with `handleBlur` (the search input's `onBlur` sets `open=false` before a `click` event would otherwise fire, closing the dropdown before a click lands) -- see `onBlur`/`handleBlur` a few lines above. But on mobile, `pointerdown` fires the instant a finger touches the screen, before the browser can tell a tap from the start of a scroll/drag gesture, and `preventDefault()` there suppresses native scroll handling for that touch too -- so any touch on a result row, including a scroll attempt, fires navigation instantly.
**Not yet fixed:** needs a fix that still beats the blur-close race on desktop (e.g. distinguish a tap from a drag by tracking pointerup position/movement delta instead of firing on pointerdown alone, or use `onMouseDown`/`onTouchEnd` with movement-threshold logic) without breaking mobile scroll. Deferred at user's request ("bug for later").

---

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

### BUG-038: Hardcover plugin's candidate scoring had no data-completeness tiebreaker, and slug lookup never ran once title search found anything
**Status:** Fixed *(2026-07-13, Chronicle.Plugin.Hardcover)*
**Symptom:** User reported Hardcover matched "Endymion" to a sparse/incomplete book record (no cover art, 0 ratings) despite hardcover.app having a properly-populated entry (507 ratings, 4.1 avg, real cover) for the same title/slug. User fixed nothing on Hardcover's side — the site already had good data; Chronicle just never found it. After the first fix below (completeness tiebreaker) shipped, a fresh automatic re-match still returned the same sparse record — `DiagnosticsJson` showed `candidatesReturned: 1`, i.e. the tiebreaker had nothing to break a tie between.
**Root cause (two parts):**
  1. `ScoreBookCandidateDirect`/`ScoreBookCandidate` scored purely on title/year/author text match — no signal for cover art or ratings, so a well-curated candidate could lose a tie to an empty duplicate purely on API result order. *(Fixed first — insufficient alone, see below.)*
  2. **The real cause:** `SearchBooksInternalAsync`'s Strategy 1 (exact `_eq` title/author lookup) called `goto done;` the moment any candidate scored ≥55 — "Endymion" scored exactly 60 on title match alone. Strategy 2 (slug-based lookup, e.g. `endymion` → the exact page the user was looking at) was *also* separately gated on `if (allCandidates.Count == 0)`. Both gates meant slug lookup never ran at all once the title index returned anything — so the completeness tiebreaker from fix #1 never got a second candidate to compare against. Hardcover's title-search index and its slug resolution can point at different underlying book rows for the same real-world title (likely a stale/orphaned duplicate vs. the maintained one) — the plugin was only ever trying the one that happened to be less complete.
**Fix:** Removed both early-exit gates. Strategy 1 (title `_eq`) and Strategy 2 (slug) now both always run and merge into the same candidate pool; every candidate — regardless of which strategy found it — still goes through the same scoring and author hard-reject, so widening the pool can only ever surface a better-populated match for the same title/author, never accept a wrong book. The completeness tiebreaker (+5 cover art, +2 ratings) now has something to actually decide between.
**Deploy trap (own mistake, resolved):** Three Hardcover plugin DLL copies existed on disk — repo-root `plugins/hardcover/`, `src/Chronicle.API/plugins/hardcover/` (what the DB's `plugins.DllPath` actually pointed at — confirmed via `PluginHostService`/`PluginService`, which load from `plugin.DllPath` verbatim, no hash check), and `src/Chronicle.API/plugins/chronicle.plugin.hardcover/` (the canonical target `scripts/RunTestEnvironment.ps1` actually builds+deploys every plugin to, including Hardcover — same short-id-vs-canonical-id split already seen once this session with TMDB). Redeployed the fix to the wrong one twice — both the completeness-tiebreaker fix and the slug-lookup fix silently never took effect until this was caught by checking `plugins.DllPath` directly against what was actually running.
**Resolved:** Rebuilt in Debug config matching `RunTestEnvironment.ps1`'s exact process (was previously building Release, an inconsistency independent of the path bug), deployed to `chronicle.plugin.hardcover/` (the script's canonical target), updated the DB row's `DllPath` to match, and deleted both now-unreferenced stale copies. `plugin_id` itself was never inconsistent (always `"hardcover"` in every manifest and in the DB) — purely a stale `DllPath` pointing at an abandoned directory, not a TMDB-style plugin_id mismatch.
**Actual root cause (found after deploy was fixed and the bug still reproduced):** With the deploy trap resolved, slug lookup was confirmed genuinely running — and still landing on the same empty record. The tell: user's own **Fix Match** with `https://hardcover.app/books/endymion` correctly resolved to `hardcover:427353` (507 ratings, real cover, full description) — a *different* book ID than what automatic search kept finding, `hardcover:506851` (empty). Both `GetBookBySlugAsync` (Fix Match's path, via `ResolveHardcoverUrlAsync` — thin, id-only query) and `GetBookBySlugFullAsync` (automatic search's Strategy 2 — a single combined query selecting every rich field) filter on the *identical* `slug: {_eq: "endymion"}`, yet returned different rows. Whatever merge/slug-reassignment happened on Hardcover's side, the thin id-only query resolves against current data; the heavier combined query was landing on a stale/orphaned duplicate — most likely a caching or materialized-view lag tied to query shape, not the filter itself.
**Real fix:** Made automatic search's slug strategy do the exact two-step Fix Match already did: resolve the id via the thin `GetBookBySlugAsync`, then fetch full detail via `GetBookByIdAsync` — instead of the one-shot `GetBookBySlugFullAsync`. Also added an edition-level image fallback (`default_physical_edition { image { url } }`) as a complementary fix, since a broader check found 66% of the user's already-matched Hardcover items have no cover art — the book-level `image` field is commonly null even for entirely correct matches; Hardcover attaches cover art to editions and the website assembles a displayed cover from one of those.
**Follow-up consolidation (user caught it: "this should literally be exactly the same code"):** The two-step slug fix above was patched into `SearchBooksInternalAsync` as its own inline copy — a second, independent hand-written version of the exact resolution `ResolveHardcoverUrlAsync` (Fix Match) already did correctly. Same class of bug as the `EnrichOneAsync`/`EnrichItemCoreAsync` split earlier this session: two implementations of one concept, free to drift apart again. Extracted `ResolveBookSlugAsync` (the two-step slug→id→detail lookup) and `BuildBookMetadata` (the `HcBookDetail`→`MediaMetadata` conversion) as the single canonical methods; Fix Match, automatic search, and the plain id-based fetch path (`FetchBookAsync`) all now call these instead of each rolling their own. This surfaced one more real bug: `FetchBookAsync`'s own `PosterUrl` never had the edition-image fallback — only the scoring function did — so even a correctly-matched book could still lose its cover on the generic post-search full-detail fetch every plugin goes through (Chronicle re-fetches full detail by ID after any search match). Deleted `GetBookBySlugFullAsync` (the query that caused the whole bug) entirely so it can't be reintroduced.
**Same audit, found the identical bug for series too:** `SearchSeriesInternalAsync`'s slug strategy called `GetSeriesBySlugFullAsync` (a combined query) while Fix Match's series-URL handling already did the correct two-step `GetSeriesBySlugAsync` + `GetSeriesByIdAsync`. Identical structure to the book bug, not yet reported only because no one had hit a case where Hardcover reassigned a series slug. Fixed the same way: extracted `ResolveSeriesSlugAsync`/`BuildSeriesMetadata`, all three call sites (Fix Match, automatic search, `FetchSeriesAsync`) now share them, deleted `GetSeriesBySlugFullAsync`. **Authors confirmed clean** — `SearchAuthorsInternalAsync` never had a slug strategy at all (only `_eq` name lookup + book-based discovery), so there was nothing to have drifted from Fix Match's author-URL handling. **Checked the other plugins** (TMDB, MusicBrainz, TheTVDB, TVMaze, FanartTV) for the same pattern — none of them accept a pasted website URL in Fix Match at all (bare numeric ID/external-ID string only), so this specific bug class is structurally confined to Hardcover, which is the only plugin with URL-paste-based Fix Match. **Known, deliberately out-of-scope residual:** `ScoreBookCandidate`/`ScoreSeriesCandidate` (dict-based scorers for Hardcover's search-endpoint fallback, `SearchBooksAsync`/`SearchSeriesAsync`) are separate from the `*Direct` typed-object scorers by necessity — different response shape from a different endpoint — so they can't share a converter. That endpoint returns 0 results today (existing comment, unrelated to this session), so it's dormant; not invested in further unless Hardcover restores it.
**THE actual root cause (found after all of the above was deployed and still didn't work):** Live log showed the slug fix genuinely finding the correct id (`Hardcover book slug 'endymion' → id=427353`) — then the final "Enrichment matched" line still showed `hardcover:506851`. The correct candidate was found and then discarded. Cause: Chronicle's audiobook hierarchy is Author(0) → Series(1) → Book(2); `ctx.ParentName` is always a book's *immediate* parent, which for a book under a series is the **series name** ("Hyperion Cantos"), not the author ("Dan Simmons") — the author is `ctx.GrandparentName`. `ScoreBookCandidateDirect`'s author-match block used `ctx.ParentName` unconditionally as "the author", so it compared 427353's real author data ("Dan Simmons") against "Hyperion Cantos", didn't match, and hard-rejected it — while 506851 (the sparse duplicate, no contributor data at all) skipped the author check entirely (only runs `if (!string.IsNullOrEmpty(authorNames))`) and kept its base title score, winning by default. The exact inversion of what the completeness tiebreaker was supposed to prevent: the missing data was what saved it. This is why every prior fix in this bug (completeness tiebreaker, slug-always-runs, shared resolution methods) was individually correct but insufficient — the real candidate was being eliminated before any of them got a chance to matter.
**Real fix:** Added `GetBookAuthorName(ctx)` — `HierarchyLevel == 2` uses `GrandparentName`, everything else uses `ParentName` as before — applied at all four places a book's author was read (the `_eq` query filter, both scoring functions, the dead search-endpoint fallback). `ScoreSeriesCandidateDirect` was already correct (a series' immediate parent genuinely is the author, 2-level hierarchy).
**Related Chronicle-core fix, found while chasing why a correct stored ID kept getting discarded:** `MetadataEnrichmentService`'s parent-ID-consistency check (`storedBase.Split('/')[0] == parentBase.Split('/')[0]`) ran unconditionally for every plugin, but is only meaningful for TMDB/MusicBrainz-style IDs built by literally appending to the parent's ID string. For Hardcover's flat, independent per-entity-type ID namespaces (book/series/author IDs are unrelated integer sequences), this comparison fails for every valid stored ID, forcing a wasteful full re-derivation on every single refresh even when the stored ID was already correct. Gated it on the same movie/tv/artist/release-group/etc. format allowlist already used earlier in the same method. All fixes deployed via the confirmed-correct path; not yet verified against the live API from this environment.

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
**Status:** Fixed *(confirmed 2026-08-22)*
**Symptom:** FanEdit plugin rows on the Metadata Assignment page show no icon. TMDB rows correctly display the TMDB colour icon. The FanEdit manifest declared `iconUrl: "https://www.fanedit.org/favicon.ico"` at the time this was reported.
**Root cause:** See BUG-019 -- FanEdit's manifest was changed 2026-05-22 to an embedded SVG data URI instead of the unreliable external favicon, independent of this file's own SVG→PNG proxy fix (492d5cc).
**Verified live 2026-08-22:** FanEdit's icon loads correctly on the Metadata Assignment page via the icon proxy (24x24 PNG, no load error).

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
**Status:** Fixed *(2026-08-22, discussed with user first per this entry's own note)*
**Symptom:** `/import` is a standalone page with "Connect Account" flows for Trakt and SIMKL. The actual import/sync work is triggered as background tasks. Having a separate Import tab feels like a duplicate of the Background Tasks page.  
**Discussed:** user chose "move Connect Account into plugin Settings; Background Tasks keeps Run Now/schedule; /import page goes away entirely."
**Found already mostly done:** `PluginsPage.tsx` already had a complete `InlineImportSection` component (account-connect PIN/QR flow, poll loop, connected/disconnected states) wired into each import provider's Configure panel — apparently built to replace `/import` but never finished. Background Tasks already has `{plugin}:import-all`/`{plugin}:delta-sync` scheduled tasks with working Run Now/Schedule for both Trakt and SIMKL — already covers the "import trigger" side too.
**Fix:** Removed the now-fully-redundant standalone `/import` route, its nav link, and `ImportPage.tsx`/`.module.css`. Deleted the now-dead `importHistory`/`importRatings`/`importWatchlist` API wrapper functions from `api/import.ts` (their only caller was the deleted page; the `ImportResult` type stays, still used by `ScanPage.tsx` for an unrelated file-scan-import concept). Verified live: nav no longer shows Import, `/import` falls through to the app's default redirect instead of 404ing.

---

### BUG-021: TMDB plugin missing icon, unconfigurable, wrong version in UI
**Status:** Partially fixed *(2026-04-20; version mismatch resolved 2026-07-13)*
**Symptom:** On the Plugins page the TMDB entry has no icon. Clicking "Configure" shows "Failed to load plugin settings. Please try again." The version shows `1.0.0` but the GitHub release is `1.0.1`.
**Root cause:** `GetSettingsSchema` endpoint returned 404 if the plugin wasn't loaded AND it never checked `ImportProviders` — so Trakt/SIMKL settings also errored.
**Fix (code):** `PluginsController.GetSettingsSchema` now checks `ImportProviders` after `MetadataProviders` and `FileScannerPlugins`.
**2026-07-13:** Manifest version was still `1.0.0` despite three later tagged releases having shipped — bumped to `1.3.0` (matching the new release, see BUG-018) and redeployed the DLL to `plugins/chronicle.plugin.tmdb/`.
**2026-08-22 check:** Confirmed fixed and verified live. Icon proxy (`/api/v1/plugins/{id}/icon`) returns HTTP 200 with real `.ico` content (15KB, themoviedb.org's actual favicon). Version is `1.3.1` (current). Health check reports `healthy: true` -- confirms the API key is configured and working, was not wiped.

---

### BUG-020: Audiobooks not available as media type in File Scan
**Status:** Fixed *(2026-04-20 code fix; remaining items confirmed done 2026-08-22)*
**Root cause:** `GetStatusAsync` only returned media types declared by the scanner's `GetSupportedMediaTypes()` (hardcoded: movies/tv/music). Any type not in that list was hidden from the dropdown.  
**Fix (code):** `GetStatusAsync` now queries all `media_types` from the DB dynamically. `ScanAsync` now falls back to the first available scanner when no scanner explicitly declares the requested type.  
**2026-08-22 check:** Both remaining items confirmed done. `GET /api/v1/media/types` returns an `audiobooks` row (id 6, hierarchyLevels 3). MusicBrainz's `supportedMediaTypes` is `["music", "audiobooks"]`.

---

### BUG-019: FanEdit icon missing in Background Tasks page
**Status:** Fixed *(confirmed 2026-08-22 -- was already resolved by an unrelated, more robust fix; tracker was stale)*
**Symptom:** The FanEdit plugin group header on the Background Tasks page shows no icon. Other plugins (SIMKL, Trakt) display their icons correctly.  
**Actual root cause (found 2026-08-22, different from the original guess):** Chronicle.Plugin.FanEdit's own `manifest.json` was changed on 2026-05-22 (commit 7fea914, "fix(manifest): replace external favicon with embedded SVG icon") to declare an inline base64 SVG data URI as `iconUrl` instead of `https://www.fanedit.org/favicon.ico` -- fanedit.org's favicon was "unreliable from server-side fetches (wrong content type or unavailable)". This sidesteps the icon-proxy question entirely for FanEdit: no external fetch happens at all anymore.
**Verified live:** On the Metadata Assignment page (BUG-014), FanEdit's icon loads via the `/api/v1/plugins/{id}/icon` proxy, 24x24, no load error. On the Background Tasks page's Scheduled Tasks group header (what this bug actually meant -- the Enrichment Status table above it has no icon column for any plugin, by design), the raw `data:image/svg+xml` URI renders directly (browsers support inline SVG data URIs in `<img src>` natively), no load error. Both confirmed via DOM inspection (`complete: true`, non-zero `naturalWidth`).

---

### BUG-018: TMDB GitHub repo has no release since 2026-03-21
**Status:** Fixed *(stale by the time this session found it — v1.2.0 already existed from 2026-04-28; cut v1.3.0 on 2026-07-13 covering the 16 commits since, including the PluginId fix this bug names and a build-breaking bug found the same day)*
**Symptom:** The GitHub repo `thegoddamnbeckster/Chronicle.Plugin.TMDB` shows no release newer than 2026-03-21, despite code changes (PluginId fix, etc.) having been deployed since then.

---

### BUG-015: TMDB missing from Enrichment Status box; SIMKL/Trakt non-functional; Trakt health check failing
**Status:** Fixed (TMDB) — SIMKL/Trakt by-design; Trakt health check root-caused, needs a user action to fully resolve
**Symptom:** TMDB does not appear in the Enrichment Status table on the Background Tasks page. SIMKL and Trakt plugins are installed but appear to do nothing. The Trakt plugin reports unhealthy despite a valid API secret key being configured.  
**Root cause (investigated):**  
- TMDB not in Enrichment Status: Two issues combined — (1) TMDB was uninstalled (see BUG-017). (2) Even after reinstall, `PluginId` in `TmdbMetadataProvider.cs` was `"tmdb"` while the catalog, enrichment rows, and database records all used `"chronicle.plugin.tmdb"`. The mismatch meant enrichment seeding wrote rows under `"tmdb"` but GetStatsAsync looked for `"chronicle.plugin.tmdb"`.  
- SIMKL/Trakt do nothing: These are **import providers** (scrobbling receivers), not metadata enrichment providers. They appear in the plugin list but not in the Enrichment Status table (which is metadata-only by design). Outbound watch-status sync to Trakt/SIMKL is a planned feature (see backlog).  
**Fix:** `TmdbMetadataProvider.PluginId` changed from `"tmdb"` to `"chronicle.plugin.tmdb"` to match the catalog and all DB records. Source manifest `plugin_id` updated likewise. DLL rebuilt and redeployed. *(2026-04-20)*
**Trakt health check -- root-caused 2026-08-22 (the OAuth-token guess above was wrong):** Live log showed both Trakt search calls and the health check getting HTTP 403 from Trakt's own API. `MetadataHealthCheckAsync` hits `/movies/trending`, an endpoint that needs no user OAuth at all -- only a valid `client_id` header -- so a 403 there means **Trakt itself is rejecting the configured client_id** (revoked, mistyped, or the Trakt API application was disabled/deleted on trakt.tv), not a stale user auth token.
**Fix (Chronicle.Plugin.Trakt, commit c96ef8a):** `MetadataHealthCheckAsync` now throws a specific message on 401/403 ("Trakt rejected the configured client_id...") instead of silently returning `false`, so the health-check UI shows that instead of the generic "Health check returned unhealthy." Verified live: `GET /api/v1/plugins/{id}/health` now returns `"Trakt rejected the configured client_id (HTTP 403) -- check the API application on trakt.tv/oauth/applications and re-enter its Client ID."`, correctly classified non-critical.
**Final root cause (confirmed by user 2026-08-22):** trakt.tv/oauth/applications shows "Creating new apps requires Trakt VIP" -- Trakt now gates API-application creation behind a paid VIP membership, and the user does not have one. A free account cannot obtain a client_id at all as of 2026; this is a Trakt platform policy change, not fixable in Chronicle's code. Documented in `Chronicle.Plugin.Trakt`'s manifest description and README (commit 7e87e45) so this is visible on the Plugins page and doesn't look like a Chronicle bug to a future reader.
**Remaining (user action, not currently possible without paying for Trakt VIP):** Either purchase Trakt VIP to create/keep an API application, or accept that Trakt import/sync is unavailable on a free account.

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
