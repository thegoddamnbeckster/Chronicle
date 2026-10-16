# Full code review: Chronicle (2026-10-15 onward)

Goal (from the owner): an end-to-end review of the whole code base, looking for **coding errors, inefficiency and redundancy**,
and making it as good as it can be. It will take several sessions over about a week. This file is the plan and the running
log; every session starts by reading it and ends by updating it.

## Ground rules

1. **Evidence before change.** A finding is written down with the file, the line and the concrete failure or cost before it
   is fixed. "Looks odd" is not a finding.
2. **Every fix ships with a test** (or an explicit note why it cannot have one). Behaviour that was wrong gets a test that
   fails first.
3. **Small, reviewable commits**, one coherent batch each, build + both test suites green before the commit. Pushed to `main`
   as they land (the owner is away; they review the log).
4. **No data is touched** without a backup being current, and nothing destructive runs against the live database from a
   script: data fixes become repair passes with tests (the Library Integrity Repair pattern).
5. **Respect the standing rules** in the memory notes: no hardcoded mappings where a DB-configurable mechanism fits; no
   writes to temp/ProgramData; plugins stay standalone; no Kodi JSON-RPC from Chronicle code; XML comments never contain `--`.
6. Don't churn: a rename or reformat that changes no behaviour is only worth doing when it removes a real redundancy.
7. The dev environment **is** production (same database). Restart the API only when a change needs it, and only after
   recording what was running (running tasks now resume after a restart).

## What counts as a finding

| Class | Examples |
|---|---|
| Correctness | wrong condition, null/edge cases, off-by-one, lost updates, races, unawaited tasks, swallowed exceptions, wrong time zone/UTC handling |
| Data integrity | operations that can orphan rows, partial writes without a transaction, unique/index gaps, destructive paths without a guard |
| Security | missing authorization, path traversal, SSRF, secrets in logs/URLs, unsafe deserialization, plugin trust |
| Efficiency | N+1 queries, whole-table loads for one row, queries the indexes don't serve, repeated work in loops, unbounded memory, blocking calls on hot paths, large payloads |
| Redundancy | duplicated logic that has already drifted (or will), dead code, unused settings/endpoints/props, parallel implementations of one rule |
| Maintainability | 3,000-line classes with several jobs, comments that contradict the code, tests that test the mock |

## Phases

Each phase ends with its findings fixed (or filed below as deferred, with a reason) and the log updated.

### Phase 0 - baseline (first)
- [ ] Build warnings by category (all projects); treat new categories as findings
- [ ] Unit + integration test run: record pass/fail/skipped, slowest tests, flaky ones
- [ ] `npm audit`, `dotnet list package --vulnerable --include-transitive`, outdated majors
- [ ] eslint/tsc clean; bundle size by chunk
- [ ] Dead-code sweep: unused public methods/classes/settings/endpoints (grep-based cross reference)
- [ ] Duplicate-code sweep: repeated blocks across services/controllers/pages

### Phase 1 - security and the API surface
- [ ] Every controller action: authentication, role checks, ownership checks (users only see/edit their own library data)
- [ ] Auth: JWT issue/validation, API keys, device auth, deactivated-user cache, password reset, rate limiting
- [ ] File and path handling: scan roots, image proxying/serving, plugin zip install, backup/restore paths
- [ ] Outbound HTTP (image fetch, plugin download, webhooks): SSRF guards, timeouts, size limits
- [ ] Secrets: plugin settings encryption, logging redaction, config files, URLs carrying tokens
- [ ] Plugin loading: integrity checks, update/approve flow, what a plugin can reach

### Phase 2 - data layer and query efficiency
- [ ] `ChronicleDbContext` model vs the queries actually run: missing indexes, redundant indexes, cascade rules
- [ ] SQLite/PostgreSQL parity (two migration sets): provider-specific SQL, case sensitivity, collation, date handling
- [ ] Hot paths measured, not guessed: library listing, people list, enrichment selection, media detail, stats endpoints
- [ ] N+1 and whole-table loads (`ToList()` then filter), projection opportunities, `AsNoTracking` on read paths
- [ ] Transaction scope: multi-step writes (merge, unmerge, reparent, repair passes) are atomic or safely resumable
- [ ] Write contention: long transactions, per-item saves in tight loops, background tasks that hold the single writer

### Phase 3 - the big services (one session each)
- [ ] `MetadataEnrichmentService` (3.9k lines): selection, locking, conflict rules, hierarchy handling; split by responsibility
- [ ] `FileScanService` + `Scan/*` (3.8k+): grouping, matching to existing items, stale/missing handling, memory on large libraries
- [ ] `MovieCollectionService`, `BookSeriesService`, `SeriesSlotTwins` and the repair passes: one rule per concept, not three
- [ ] `SyncOrchestrationService`, `ScrobbleService`, `PersonResolutionService`, `MergeService`/`DuplicateCleanupService`
- [ ] `TaskSchedulerService`, `TaskPriorityGate`, notifications and live updates

### Phase 4 - API controllers
- [ ] `MediaController` (2k), `ScraperController` (2k), `PluginsController`, `PeopleController`, `SettingsController`, others:
      thin controllers (logic belongs in services), consistent envelopes/errors, validation, pagination limits

### Phase 5 - plugin host and SDK
- [ ] `Chronicle.Plugins` contracts, `PluginHostService`/`PluginService`/registry, manifests, settings schema, task discovery
- [ ] Plugin authoring docs match the code (they have drifted before)

### Phase 6 - web client
- [ ] API client layer: error handling, auth refresh, request dedup, query keys/invalidations that over- or under-refresh
- [ ] Large pages (`MediaDetailPage` 1.7k, `BackgroundTasksPage`, `PluginsPage`, `ScanPage`, `PeopleLibraryPage`): split, memoise, re-render cost
- [ ] Bundle and load: code splitting, image loading, polling intervals (several pages poll every 1-3 s)
- [ ] Accessibility basics, mobile layout, theming consistency (the dropdown bug was a theme-wide gap)

### Phase 7 - tests
- [ ] Tests that cannot fail (assert on mocks), duplicated fixtures, slow tests, integration coverage of critical paths
      (scrobble flow, auth, plugin loading, merge/unmerge, scan)

### Phase 8 - sibling repositories
- [ ] Kodi add-ons (`Chronicle_Scraper`, `Chronicle_Scrobbler`, `Chronicle_Rating*`): Python 3 correctness, Kodi API misuse, error paths
- [ ] Audiobookshelf services
- [ ] Plugins, highest value first: Hardcover, MusicBrainz, TMDB, OpenLibrary, TheAudioDB, Wikipedia, IMDb, then the rest
      (shared patterns: rate limiting, retries, HttpClient lifetime, settings, caching)

### Phase 9 - cross-cutting and docs
- [ ] Logging: volume, levels, secrets, consistency; configuration and defaults; documentation accuracy

## Order of work and expected effort

Phase 0 (this session), Phase 1 and 2 (sessions 2-3: highest risk and highest payoff), Phase 3 (sessions 3-5), Phase 4-5
(session 6), Phase 6-7 (session 7), Phase 8-9 (sessions 8+). Findings found early that change how later phases should be
done reorder this list.

## Progress log

Newest first. One entry per working session: what was reviewed, what was found, what was fixed (commit), what was deferred.

### 2026-10-16 (session 2, owner asleep)
- Owner's instructions: sign-up lock, culture handling, disposable locks, slow endpoints, remaining phases, code review of the fixes; delete the old
  database copies (17 GB, done) and superseded add-on zips in C:/temp and I:/videos (done, newest of each kept).
- Fixed this session (all with tests, pushed): F1-F28 in the register below. Highlights: admin-controlled sign-up lock (default open;
  `Chronicle.API --unlock-registration` reopens it with no login), invariant culture pinned, four undisposed locks, enrichment stats cached 5 s,
  Kodi episode lists re-check the provider hourly not per request, plugin auth-failures by index seek, six redundant indexes dropped, route-level
  code splitting (entry bundle 473 kB -> 116 kB), folder browsing and scanning now admin-only, plugin catalog entries validated, image URL
  validator no longer drops images on timeouts/429 and no longer fires 60 requests at one CDN, startup folder-path backfill runs once,
  duplicate cleanup logs one line instead of 11,000, Deezer error 500.
- Evidence sources: the request log (per-endpoint totals), the slow-command log, a warning/error histogram of three days of logs, EXPLAIN on a
  read-only copy of the live database, and the analyzers dump.
- Phases touched: 0 (baseline done: build, tests, lint, tsc, bundle), 1 (controllers audit done; auth/files/SSRF/secrets reviewed), 2 (indexes,
  N+1 sweep, whole-table loads), 6 (bundle, polling inventory), 8 (plugin-wide grep for HttpClient/blocking/async-void: clean), 9 (log volume).
- Not yet reviewed in depth: the line-by-line read of MetadataEnrichmentService, FileScanService, ScrobbleService, SyncOrchestrationService; the
  Kodi add-ons (Python); individual plugins beyond the grep sweep; accessibility pass; test-quality pass.

### 2026-10-15 (session 1)
- Plan written. Starting Phase 0.
- Phase 0/1/2 partial. Fixed: missing `media_enrichment (PluginId, Status, MediaItemId)` index (full scans, 214 s waiting in 40 min); API-key `LastUsedAt` write no longer blocks auth; vulnerable `System.Security.Cryptography.Pkcs` pinned; startup seeding churn (Phase 1 inserted + Phase 3b deleted ~11k rows per restart; Phase 2 loaded ~4 GB JSON; compact key set; streaming JSON key check).
- Filed, not yet fixed: open registration has no off switch (AuthController.Register / UserService.RegisterAsync); culture-sensitive string ops (CA1304/1305/1310/1311; worst: MetadataEnrichmentService, MediaService, FileScanService, PasswordResetService, SyncOrchestrationService); CA2016 (LibraryController:52, FileScanService:2641, ScheduledScanService:192, TaskSchedulerService:85); CA1001 undisposed locks (AssignmentConfigCache, DatabaseAdminService, FieldAliasCache, PluginCatalogSource); CA1869 x5; BookSeriesService unused `scopeFactory`; EF1002 in DatabaseAdminService:228 / DatabaseCopier:295; unfiltered loads at PeopleController:524, DuplicateCandidateScanService:70, ScrobbleService:831, MisparentedChildRepairService:35; slow endpoints: plugins/auth-failures, enrichment/stats polling, media/search (3.3 s), scraper tv/episodes (1.2 s).
- Needs owner consent: ~17 GB of old `chronicle-dev.db.bak/before-*` copies in src/Chronicle.API.

## Findings register

| # | Phase | Finding | Evidence | Status | Commit |
|---|---|---|---|---|---|
| F1 | 2 | `media_enrichment` had no index for "rows of plugin X in status Y" | slow-command log: 214 s waiting in 40 min; stats endpoint 1.4 s x 2,535 | fixed | e4c14e4 |
| F2 | 1 | API-key `LastUsedAt` write inside the auth handler blocked Kodi polls for 34 s | request log | fixed | 75a0de0 |
| F3 | 0 | `System.Security.Cryptography.Pkcs` 10.0.0 advisory GHSA-846f-f764-76mx | `dotnet list package --vulnerable` | fixed (pinned 10.0.13) | e4c14e4 |
| F4 | 2 | Startup seeding created then deleted ~11,000 enrichment rows, loaded ~4 GB of JSON and ~16M strings every restart | log + code | fixed | 90645f9 |
| F5 | 2 | Duplicate scan loaded all 720k external ids | code | fixed | eed87a2 |
| F6 | 1 | Open registration, no off switch; two sign-ups on an empty install could both become admin | code | fixed (setting + CLI unlock + serialized) | 3255931 |
| F7 | 9 | Server behaviour depended on the host locale (Turkish dotless i, comma decimals) | analyzers: 113 sites | fixed (invariant culture pinned) | 3255931 |
| F8 | 2 | Four singleton `SemaphoreSlim`s never disposed (CA1001) | analyzers | fixed | 3255931 |
| F9 | 2 | Enrichment stats recounted 1.5M rows on every poll | request log | fixed (5 s shared cache, invalidated on reset/skip) | (perf commit) |
| F10 | 2 | `plugins/auth-failures` walked the whole enrichment index | request log 0.55 s x 77 | fixed (per-plugin seek) | (perf commit) |
| F11 | 2 | Kodi `tv/episodes` hit the metadata provider for every season on every request (0.8-8 s) | request log | fixed (hourly per show) | (perf commit) |
| F12 | 2 | Six redundant indexes on hot tables | sqlite_master prefix analysis | fixed (SQLite + PostgreSQL migrations) | (index commits) |
| F13 | 1 | Any signed-in user could list directories anywhere on the server (`/filesystem`) | controller audit | fixed (Admin) | (security commit) |
| F14 | 1 | Any signed-in user could preview/import arbitrary paths and create scan folders | controller audit | fixed (Admin) | (security commit) |
| F15 | 1 | Plugin catalog entry fields reached a file path and a URL unchecked; download unbounded | code | fixed (CatalogEntryGuard, 200 MB cap, api.github.com only) | (security commit) |
| F16 | 6 | One 473 kB entry bundle for every page | build output | fixed (116 kB) | (web commit) |
| F17 | 9 | Image URL validator: 2 requests per URL, 60 at once per album, and a timeout or 429 deleted a valid image | code + log (134 drops) | fixed | (maintenance commit) |
| F18 | 2 | `BackfillFolderPaths` LIKE-scanned all item metadata at every start | startup log | fixed (once) | (maintenance commit) |
| F19 | 9 | Duplicate cleanup logged one warning per unmergeable pair (11,726 on one night) | log histogram | fixed (one summary) | (maintenance commit) |
| F20 | 3 | Duplicate merge blocked when one provider's artist id met another record's album id | data: ~8k track pairs | fixed (kind-aware conflict) | (maintenance commit) |
| F21 | 8 | Deezer error 500 ("wrong/empty parameter") failed items every pass | log histogram | fixed in plugin repo | 43b1eb1 (Deezer repo) |
| F22 | 9 | Health checks logged 6,000 lines a day | log histogram | fixed | c9e9f3f |
| F23 | 2 | Paged queries without `OrderBy` (EF warning x95) | log | fixed | (maintenance commit) |
| F24 | 0 | Time-of-day dependent scheduler test (failed 03:45-04:00 UTC) | test run | fixed | 3255931 |
| F25 | 0 | CA1869 repeated `JsonSerializerOptions` (5), CS9113 unused parameter, CA2016 (2) | analyzers | fixed | (chore commit) |
| F29 | 3 | A root scan group matched ANY level of the type by name: the band "Genesis" matched a track called "Genesis", and 94 albums were filed under it (also Ghost, Rush, NIRVANA) | live data: 94 level-1 items under level-2 parents | fixed (roots match roots) + repair pass; 84 items moved, 4 artists created, enrichment seeded | (library commit) |
| F30 | 3 | `MediaItemMatcher.FindByTitleYearAsync` (scrobble/import/sync) searched every level, so a show title matched an episode and its season was created under the episode | data: 10 stray "Season N" under episodes | fixed (+ sync stub twin lookup) + repair pass (move up / remove empty copies) | (matching commit) |
| F31 | 3 | Merge overwrote the loser's media type before logging it, so an Unmerge brought the audiobook author "Iain M. Banks" back as a person with 5 series under him; cross-type merges could hang children under a wrong-type parent | merge log + data | fixed (+ refusal rule) + repair retypes the stub | (library commit) |
| F32 | 2 | The scan's name tier loaded every item of the type (with blobs) for every group, even already matched ones | code | fixed | (scan commit) |
| F33 | 2 | `SeedEnrichmentRowsForNewItems` read every row of each plugin (~90k ids x 17) per batch | code | fixed (chunked) | (library commit) |
| F34 | 2 | `ResetAsync` loaded a plugin's whole queue into the change tracker | code | fixed (one UPDATE) | (enrichment commit) |
| F35 | 2 | SQLite `synchronous=FULL` under WAL: one disk wait per commit on the busiest writers | design | fixed (NORMAL) | (sqlite commit) |
| F36 | 9 | Permanent INFO line per Kodi details request (4,825/day, avg 3 ms) | log histogram | fixed (Debug unless > 500 ms) | (chore commit) |
| F37 | 7 | Test quality sweep: 8 of ~2,500 tests have no assertion (all "does not throw") | script | accepted | n/a |
| F38 | 6 | `exhaustive-deps` is off; the 11 sites it would flag are load-once patterns | eslint one-off | accepted | n/a |
| F39 | 8 | Plugin repos: no `new HttpClient()` misuse, blocking calls or async void; suites for Hardcover, MusicBrainz, TMDB, OpenLibrary, TheAudioDB, Wikipedia, IMDb, Deezer, FanartTV pass | grep + test runs | verified | n/a |
| F40 | 8 | Kodi add-ons: pyflakes clean; the movie and TV scraper add-ons carry copies of the same client/sync modules (5 have already drifted) | diff | deferred (needs a shared module vendored by build.ps1) | n/a |
| F26 | 1 | Raw SQL with interpolation (EF1002) in backup/copy code | analyzers | verified safe: values are generated, quoted | n/a |
| F27 | 1 | Zip extraction | code | verified: .NET 9 `ExtractToDirectory` rejects path escapes; restore validates entry names | n/a |
| F28 | 1 | Secrets in logs / URLs | grep + log sample | verified: none | n/a |

## Open items, decisions for the owner

| # | Item | Why it is open |
|---|---|---|
| O1 | Any signed-in user can edit or delete shared media (`DELETE /media/{id}`, metadata overrides, merge) | Shared library is by design, but "ordinary users cannot delete everyone's media" is a product decision. Gating deletes to Admin is one attribute plus tests. |
| O2 | Downstairs Shield (10.0.0.203) sends an invalid/expired API key about every 3 minutes (1,353 rejections in 3 days) | Needs a new key on that device (Settings > API Keys); nothing to fix server-side. |
| O3 | Some duplicated album subtrees (e.g. "The Doobie Brothers" albums 414426 / 806909) created when a library folder was renamed and rescanned | The scanner created new track items instead of matching the moved files. Needs the FileScanService review (Phase 3). |
| O6 | 9 harmless orphan rows (5 external ids, 4 library rows of deleted items) from before foreign keys were enforced | Not worth a pass; they cannot be reached. |
| O5 | `tag_mismatch_rematch` runs every minute and writes a run record each time | Cheap, but a queue-driven trigger would remove ~1,400 writes a day. |

## Deferred (with reason)

_Nothing deferred without an entry above._
