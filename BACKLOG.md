# Chronicle — Backlog

Items collected from dev sessions. Roughly priority-ordered within each section.

**Last audited against the code: 2026-10-07.** Designs for the unbuilt items: `docs/plans/2026-10-07-backlog-designs.md`. Done items from the old list were moved to "Completed" at the bottom. Items tagged *(unverified)* were not found in a quick code search, but may exist under a name I didn't look for.

---

## File Scanner

- **Media type per file** — FileScanner should determine the media type of each file itself; it shouldn't matter what kind of media a folder holds. Speed is secondary (background process). Today the scan request still takes a `MediaTypeId` (`FileScanService.cs`), and the Audiobooks and hierarchical paths branch on it.
- **Scan results: type mismatch correction — DONE (flag, not auto-reclassify).** A scan group whose files look like another media type (per `media_types.ScanHintsJson`, editable on Settings -> Media Types) shows a warning and a "Switch to X and rescan" button on the scan page; the nightly scan holds such groups back and raises a `scan.review` notice (`scan.mismatch_action` = `flag` default | `ignore`). See `docs/SCANNING.md`. Not built: automatic reclassification of already-imported items.
- **Related-files assumption — DONE for extras (subtitles, artwork, theme music, extras folders, booklets).** The scan page shows "+N related files" and a "Remember subtitles, artwork and extras" checkbox; the nightly scan follows `scan.bundle_related_files`. Rows live in `media_item_related_files`, shown on the item page. The extension/folder lists are settings (`scan.sidecar_*`). Not built: bundling *every* file in a matched item's folder (only files already classed as extras are kept) and a per-scan-folder choice (the setting is global).
- **Notify when background scans find new items — DONE** (the bell; administrators; mutable per person).
- **Flexible pattern matching — DONE (release-style names).** Download-style folder and file names (`Show.Name.S02E03.720p.HDTV.x264-GRP`, `Movie.Name.2019.1080p.BluRay`, `[Group] Show - 1x05`) are read for title, year, season and episode; loose episodes in the scan root are filed under a derived show; derived results are held below the auto-import threshold so a person reviews them first. See `docs/SCANNING.md`. Absolute-numbered anime (`Show - 112`) and date-named daily shows are covered too (see `docs/SCANNING.md`).
- **User-defined file types — DONE.** `scan.extra_video_extensions` / `scan.extra_audio_extensions` (Settings -> Library -> Scanning) for the grouped scan, and matching settings in the FileScanner plugin (committed in its repo, not yet released). See `docs/SCANNING.md`.
- **Music filename parsing — DONE (checked, then fixed).** Confirmed gap: for untagged music the grouped scan used the whole file name as the track name ("01 - Enter Sandman") and only read a leading number. Now `TrackFileName` reads "01 - Title", "01. Title", "1-02 Title" (disc-track), "Track 05 - Title" and "Artist - 01 - Title" for audio files only, strips the number from the name and fills track/disc numbers; titles that merely start with a number ("99 Problems") are left alone, and TV episodes are never touched. Embedded tags still win when present. Not changed: the FileScanner plugin's own flat scan (`FileNameParser.ParseAudio`) still uses the raw name plus tags.

---

## Library / Media Detail

- **Live poster updates — DONE.** The shell polls a change feed every 15 s (visible tabs only); item pages refresh immediately and the library grid at most every 30 s. See `docs/NOTIFICATIONS_AND_LIVE_UPDATES.md`. Not covered: bulk statements that bypass change tracking call `MarkAllChanged`; any new one must too.
- **Local images — DONE.** The local poster was already served (`/media/{id}/local-poster`); artwork found beside an item (recorded as a related file) is now served too (`GET /media/{id}/related-files/{fileId}/content`, artwork kind and raster image types only) and shown as thumbnails under "Related files".
- **All file paths — DONE.** The item page lists every file or folder the scanner recorded (`GET /media/{id}/files`), with size and a "not found on disk" flag. There is no separate internal Chronicle copy of media files to list: Chronicle tracks files where they are.

---

## Plugins

- **Plugin integrity — DONE (hash + allowlist).** Manual install and folder auto-registration now honour the catalog allowlist (bypass closed); plugin files are hashed at install/update and re-verified on every load; changes are blocked, notified and approvable. See `docs/SECURITY.md`. Not built: author signing (`docs/FEATURE_PLUGIN_SECURITY.md`). Plugin authors: `docs/PLUGIN_DEVELOPMENT_GUIDE.md`.
- **Bundled plugins** — FileScanner must ship with the Chronicle install (separate repo, DLL included). Chronicle's releases currently ship source/tag only (see CLAUDE.md), so confirm the installer story.
- **Catalog source** — The old item asked for a `plugins.json` in the repo. What was built instead is `PluginCatalogSeeds.cs`, a hard-coded list of repos, resolved live from each repo's latest GitHub release and manifest. Adding a plugin therefore still needs a code deploy. Decide whether to move the seed list out to a hosted file.

---

## Database

- **Backups and maintenance — DONE (Settings -> Database).** Nightly + on-demand zipped backups, retention, download/upload/validate/restore (type RESTORE; safety backup; restart swaps the file in), quick/full maintenance as scheduled tasks, size reporting and warning, SQLite scratch files kept beside the database. Remaining: backups are SQLite-only; very large downloads go through the browser's memory (a streamed/signed download link would fix it); the restart relies on Docker / the service manager / the dev script to start Chronicle again.
- **Size limits / SQLite → Postgres** — Monitor DB size; offer migration to Postgres (a compose file exists: `docker-compose.postgres.yml`) or ways to free space if the user stays on SQLite.
- **Migration scripts — decided: not building.** EF Core code-first migrations (107 so far, auto-applied at startup) are the upgrade path. Backup/restore is the way back. A full-schema script can be generated from EF if ever needed.

---

## Users

- **Password reset — DONE.** Email link (Settings -> Email), administrator-issued codes (Users -> Reset Code), and the local `--reset-admin-password` command; single-use hashed tokens, throttled, audited, ends sessions. Remaining: per-user "email me a link" from an admin's screen, and optional email notification when a password is changed.

---

## Media Types

- **DONE** — Settings -> Media Types (add / edit / switch off / delete-if-empty / hand back to plugins), per-type action word and level names drive the wording of the item pages, the audiobook scan is a property of the type (not its name), and an administrator's edits are protected from plugin updates. See `docs/MEDIA_TYPES.md`.
- **Remaining hard-coded wording - DONE.** The credits heading is a per-type setting, and the provider-family guessing in the scanner and enrichment code (anime -> tv ...) now reads `media_types.ProviderFamily`. `AddCollectionPage` still prefers a type named "movies" only as a default selection (it falls back to the first flat type).
- **Plugin catalog by media type - DONE.** Manifests may list `supported_media_types`; the catalog falls back to its tags, and `GET /plugins/catalog?mediaType=` plus a filter on the Plugins page narrow it (a type's provider family counts).

---

## Substack Plugin (not started — no repo or code found)

- Pull subscribed podcasts and episodes.
- Track listened episodes and progress.
- Locate a podcast from inside Chronicle.
- Scrobble source: a podcast played on the Substack site is reported to Chronicle as listened.

---

## Security

Design and audit: `docs/plans/2026-10-07-session-keys-design.md`.

- **Re-scope existing API keys - DONE (2026-10-08).** The 18 Kodi/Vision keys are `device`, the Audiobookshelf bridge key is `bridge`.
- **Set `Security:TrustedProxies`** only if Chronicle is ever put behind a reverse proxy / moved to Docker (not the case today).
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
