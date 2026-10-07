# Chronicle — Backlog

Items collected from dev sessions. Roughly priority-ordered within each section.

**Last audited against the code: 2026-10-07.** Designs for the unbuilt items: `docs/plans/2026-10-07-backlog-designs.md`. Done items from the old list were moved to "Completed" at the bottom. Items tagged *(unverified)* were not found in a quick code search, but may exist under a name I didn't look for.

---

## File Scanner

- **Media type per file** — FileScanner should determine the media type of each file itself; it shouldn't matter what kind of media a folder holds. Speed is secondary (background process). Today the scan request still takes a `MediaTypeId` (`FileScanService.cs`), and the Audiobooks and hierarchical paths branch on it.
- **Scan results: type mismatch correction** — If the movie scanner detects something that looks like TV (S01E01, etc.), re-classify and match against the correct type. *(unverified)*
- **Related-files assumption** — Checkbox on the scan/review page: "Treat all files in a matched item's folder as related to that item." Files sharing a folder with a confidently matched item (e.g. `Dark Matter (2024)\theme.mp3`) are bundled onto it as related/attached files (shown on its detail page) instead of surfacing as separate low-confidence items. Applies to TV (theme, artwork, extras), movies (subtitles, featurettes) and albums (booklets, cue sheets). Partial groundwork: `ScanGroupingService` already drops sidecar extensions and supplemental folders from grouping, but nothing attaches them to the parent and there is no toggle.
- **Notify when background scans find new items** — `ScheduledScanService` runs scans on a schedule; there is no user-facing notification when it finds something new. *(unverified)*
- **Flexible pattern matching** — Handle messy/unorganised folder structures (e.g. `E:\Video Downloads\MCM Download Parser`). Smarter fallback when standard patterns fail.
- **User-defined file types** — FileScanner plugin: let the user add their own filetypes.
- **Music filename parsing** — Audio extensions are supported (`FileNameParser.cs`); confirm Artist - Album - Track filename parsing is also covered, or whether it relies only on embedded tags (`EmbeddedTagReader.cs`).

---

## Library / Media Detail

- **Live poster updates** — When background refresh updates an item, the library view should reflect it without a full reload. Only `NowPlayingBanner` polls today. *(unverified for the library)*
- **Local images** — Image thumbnails work for remote art; local images still need the backend to serve them.
- **All file paths** — Detail page shows the single `fileScannerMeta.filePath`. Still needed: every associated file (internal Chronicle store and original on-disk path), listing all of them for multi-file items (cuts, episodes).

---

## Plugins

- **Plugin integrity** — Intent: unknown plugins must never get onto the system. Allowlist (installs only from `PluginCatalogSeeds.cs` repos) already exists. Replace the removed SHA-256 pinning (2026-09-04) with: (1) hash the DLL at install and store it; (2) re-verify on every load to catch on-disk tampering; (3) show the admin when a hash changes on update; (4) later, author signing (`docs/FEATURE_PLUGIN_SECURITY.md`). First audit whether the manual DLL install route (`POST /api/v1/plugins`) bypasses the allowlist.
- **Bundled plugins** — FileScanner must ship with the Chronicle install (separate repo, DLL included). Chronicle's releases currently ship source/tag only (see CLAUDE.md), so confirm the installer story.
- **Catalog source** — The old item asked for a `plugins.json` in the repo. What was built instead is `PluginCatalogSeeds.cs`, a hard-coded list of repos, resolved live from each repo's latest GitHub release and manifest. Adding a plugin therefore still needs a code deploy. Decide whether to move the seed list out to a hosted file.

---

## Database

- **Backups** — Keep 10 rolling zip backups; expose them as downloads; accept an uploaded backup, validate it, restore it via the UI, then restart and reload on the new DB. No backup code found. *(unverified)*
- **Maintenance** — Automatic background maintenance (index rebuilds, statistics, etc.), also runnable manually from a Settings → Database section, with last-run times recorded. No maintenance code found. *(unverified)*
- **Size limits / SQLite → Postgres** — Monitor DB size; offer migration to Postgres (a compose file exists: `docker-compose.postgres.yml`) or ways to free space if the user stays on SQLite.
- **Migration scripts — decided: not building.** EF Core code-first migrations (107 so far, auto-applied at startup) are the upgrade path. Backup/restore is the way back. A full-schema script can be generated from EF if ever needed.

---

## Users

- **Password reset** — Email-based reset link, or an admin-issued one-time token for installs without SMTP; single-use, expires (~1h); SMTP configurable under Settings → Email. Only the admin set-password route exists (`UsersController.ResetUserPassword`). No self-service reset, tokens or SMTP settings found. *(unverified)*

---

## Media Types

- **User-registered media types** — Plugin-driven registration already works (a plugin's `MediaTypeSupport` is upserted into `media_types` at startup). Missing: a Settings → Media Types page so the user can add/edit types with no plugin, a "no plugin handles this type" prompt linking to the catalog, and protecting user edits from the startup upsert. See `docs/plans/2026-10-07-backlog-designs.md` §5.
- **No hardcoding** — Each remaining case is a bug. Interaction verbs ("Watch"/"Listen"/"Read") come from the media type row, not code: fix Dashboard "Watch Time"/"Total Watch Time" and HistoryPage "Watch History". `FileScanService`'s `"audiobooks"` name check becomes a media-type property.

---

## Substack Plugin (not started — no repo or code found)

- Pull subscribed podcasts and episodes.
- Track listened episodes and progress.
- Locate a podcast from inside Chronicle.
- Scrobble source: a podcast played on the Substack site is reported to Chronicle as listened.

---

## Security

Design and audit: `docs/plans/2026-10-07-session-keys-design.md`.

- **Re-scope existing API keys** — keys created before scopes existed are still `full`. On Settings -> API Keys, change each Kodi / scrobbler key to `device` and the Audiobookshelf bridge key to `bridge`. (Left unchanged automatically so no device could break.)
- **Set `Security:TrustedProxies`** for the production Docker deployment (see `docker-compose.yml`), so only the reverse proxy can supply a client address.
- Done 2026-10-07: login/registration/pairing throttling, API-key scopes, session cookie for image routes, forwarded-header trust option, injectable audit log, plugin icons fetched through the filtered fetcher; earlier: session keys (restart ends all sessions; logout; session list; password change/deactivation end sessions), F-1 diagnostics now authenticated, F-2 poster proxy locked down, F-3 progress endpoints authenticated, auth/connection logging.

---

## Completed

**File Scanner**
- Browse button on path inputs (`PathInput` + `FolderPickerModal`).
- Persistent scan folders (`ScanFolderController` / `ScanFolderService`) with scheduled background scanning (`ScheduledScanService`).
- Scan progress feedback (`ScanProgressService`).
- Accept/reject individual detected groups on the scan page (`ScanPage` / `ScanGroupCard`).
- Media type badge in scan results; confidence-score tooltip.
- Music audio extensions (.mp3, .flac, .m4a, .ogg, .wav, .aac and more) in FileScanner.
- FileScanner plugin now at v1.2.1.

**Library / Detail**
- Per-provider metadata boxes (`PluginMetadataBox`, generic JSON renderer, enrichment drill-down).
- Metadata assignment/precedence (`docs/METADATA_ASSIGNMENT.md`), artwork pinning with overrides at five reset scopes.
- Library: clickable cards, type badge, status filter, 9 sorts, per-section paging, presets, TMDB rating, fold-scoped prev/next, hierarchical Up button, single and multi-select delete, merge/dedup.
- Image thumbnails (remote), broken-image fallback everywhere, sidebar indentation.
- Add Media with scraper-backed search; Refresh for items with no external ID; collections and movie sets.

**Users**
- Settings → Users: My Profile and Manage Users (add, deactivate, delete, admin password reset, API keys, last-admin guard, contacts).
- Auth: global `AuthContext`; JWT plus API keys; device auth.
- Roles/user types beyond Admin (readonly, metadata editor) — *check*; only the Admin role was seen in controllers.

**Plugins**
- Catalog with install, update and uninstall from GitHub releases, plus an update-check task.
- Plugin set: 80+ sibling plugin repos exist on disk.

**Background services**
- Settings → Background Tasks page (`BackgroundTasksPage`): last/next run, Run Now, per-plugin tasks, scheduler.
- Metadata refresh service (configurable interval).

**Other**
- Dashboard, Reports, History, People, Lists (inline rename, click-through), Stats.
- Dark Teal theme and the Themes plugin.
- Dev startup script `scripts/RunTestEnvironment.ps1` (API 7979, web 8888, ABS bridge 9877).
