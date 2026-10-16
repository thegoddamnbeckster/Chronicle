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

### 2026-10-15 (session 1)
- Plan written. Starting Phase 0.

## Findings register

| # | Phase | Finding | Evidence | Status | Commit |
|---|---|---|---|---|---|

## Deferred (with reason)

_None yet._
