# Chronicle — Backlog

Items collected from dev sessions. Roughly priority-ordered within each section.

---

## File Scanner

- **Browse button** — Every path input in the UI needs a folder-picker button. No exceptions. No manual typing of paths.
- **Persistent scan folders** — Scan paths saved to DB/settings. Pre-populated on the scan page. Scanning re-runs automatically on a background schedule (configurable interval).
- **Media Type** - It should not matter what kind of media is in a particular folder (although that is convenient).  The FileScanner plugin should determine the media type of each file.  Speed does not the most important factor as this should primarily be a background process.
- **Background scanning** — Scans run silently in the background and notify the user when new items are found.
- **Scan progress feedback** — Show current folder being scanned in real time, not a frozen spinner.
- **Music support** — FileScanner plugin: add audio extensions (.mp3, .flac, .m4a, .ogg, .wav, .aac), music filename parsing (Artist - Album - Track, etc.), register "Music" as a supported media type.
- **Other file support** - FileScanner plugin: allow user to add their own filetypes.
- **Flexible pattern matching** — Handle messy/unorganized folder structures (e.g. `E:\Video Downloads\MCM Download Parser`). Smarter fallback when standard patterns fail.
- **Scan results: accept items** — From the scan results page, user can approve/reject individual detected items before they're imported.
- **Scan results: show media type** — ✓ Implemented: `mediaTypeHint` badge shown in Preview table (Type column) and Review list rows.
- **Scan results: type mismatch correction** — If the movie scanner detects something that looks like TV (S01E01, etc.), automatically re-classify and match against the correct type.
- **Confidence score info** — Show the scoring formula somewhere accessible (e.g. a small info popup on the scan page). Formula: NFO+ID=100, Title(Year) filename=85, NFO+title+year=85, dotted/spaced filename=70, title only=50.
- **Related-files assumption** — When scanning for movies, a checkbox option: "Assume all files in a folder containing a matched movie are related to that movie (images, NFO, subtitles, etc.)." Similar per-media-type rules for TV shows (all files in a season folder belong to that season), Music Albums (all files in an album folder belong to that album), Audiobooks, etc. The rule is: opt-in permission to treat the containing folder as a media bundle rather than individual file matches.

---

## Library

- **Indent sub-items in tree** — When showing hierarchical media (show → season → episode), indent child items visually so the parent-child relationship is obvious.
- **Content** - as media is added ot the library, the library should display it dynamically.
- **Metadata** - Metadata can be downloaded by any metadata plugin, not just TMDB.  Each set of metadata per media item shall be shown in it's own box labelled for that metadata provider (TMDB, Trakt, SIMKL, TinyMediaManager, LastFM, etc).  Each box will be labelled for the metadata provider's name and have the metadata provider's icon.  

---

## Lists

- **Editable list name** — ✓ Already implemented (click title to rename inline)

---

## Substack Plugin

- Pull all subscribed podcasts and their episodes.
- Track which episodes have been listened to and at what progress.
- Locate a podcast from within Chronicle.
- Scrobble source: if a podcast is played through the Substack website, report it to Chronicle as listened.

---

## User Management

- Settings section for managing users: add new users, set/reset passwords, associate API keys to users.
- Admin-only. First registered user is already admin.
- May specify user type (readonly, admin, metadata editor, etc)

---

## Media Detail

- **All file paths** — Display every file path associated with a media item in the metadata page, both internal (Chronicle's data store) and external (original path on disk). If multiple files exist (e.g. different cuts, multiple episodes), list them all.
- **Image thumbnails** — ✓ Implemented: TMDB poster/backdrop shown as 80px thumbnails; clicking opens full size in new tab. (Local images still outstanding — requires backend to serve them.)

---

## Plugins

- **Plugin catalog from GitHub** — Move the plugin catalog out of the hard-coded server array and into a `plugins.json` file stored in the Chronicle GitHub repo. The server should fetch this file at startup (or on demand) so new plugins can be listed by editing the file, without a code deploy. Users should be able to browse the catalog and choose which plugins to install.
- **Adding and removing** - the user should be able to add new plugins based on plugins.json from github and remove them from the local Chronicle app.  Chronicle should immediately reflect the changes and add or remove menu items and UI elements depending on the plugin.
- **plugin files** plugins are to be built in their own repos.  Finished files and their hashes are to be registered in plugins.json.
- **Security ** When downloading files from github, Chronicle will confirm that the hash at github matches the downloaded file's hash.  Downloaded files will be treated as hostile until they can be verified as safe - we must ensure that the user's computer is never compromised.  Security is paramount.  If this means scanning the file with an external security service, then this should be an option.  Either locally or online.
- **included plugins** - Filescanner must be included as part of the Chronicle install.  Filescanner will remain a separate project and repo, but the dll needs to be included with the main Chronicle installation.

---

## General UI

- **No broken images** — All image elements need `onError` fallback to the letter-placeholder. Applied to: LibraryPage ✓, MediaDetailPage ✓, AddMediaPage ✓, PluginsPage ✓, ListDetailPage ✓, MediaDetailPage child grid ✓. All pages covered.

---

## Database

- **Database Migration** - Chronicle will maintain a database schema build script.  Each version change of the database requires it's own unique database build script and upgrade and downgrade scripts for the previous version to the current version.  This will be updated with any new additions to the database as they're added.  As versions change, upgrade and downgrade scripts must also be provided for each version.  These will be version to version.  If version 3 adds a table, then if the user is at version 2, they will run the upgrade script to get to version 3.  If they desire to return to version 2, they will run the downgrade script.  Brand new installs, or when the user wishes to initialize their database, they would use the full schema build script.
- **Maintenance** - Chronicle will maintain it's own database in the background automatically.  Rebuilding indexes, updating statistics...whatever needs to be maintained.  Chronicle will do this automatically.  The user may run these manually also, so this functionality needs to be exposed in a Database section under settings.  We must keep track of the last time that these maintenance steps were taken.
- **Database constraints** - Chronicle must monitor the size of the database.  If the database becomes too large for SQLite to handle, then Chronicle needs a way to migrate from sqlite to a more robust database system that is capable of handling the data.  Should the user wish to stay with sqlite, then Chronicle needs to provide options to free space in the database so that it can run.
- **Database backups** - Chronicle should maintain 10 backups of it's database as a zip file.  The interface should expose the backups as downloads.  The user should be able to upload a backed up file and it should then be checked to ensure that it is a valid backup and then allow a restore to occur autmoatically through the interface.  The interface should then restart and reload itself using the new database as the current database file.

--

## Media

- **Media Types** - The user may register any media type.  If there is a plugin available for that media type, Chronicle should ask to download something for it.
- **Adding a plugin** - Adding a plugin that will handle a specific media type (or types) will register it automatically within Chronicle.  Chronicle will then use that plugin to scan existing media (if appropriate) and that metadata will then become available to Chronicle.
- **Default media types** - Default media types will be Movies, Music and TV.  Plugins for these will be included as part of the Chronicle install, but they will be subject to updates as they become available.
- **No Hardcoding** - Chronicle will not be hardcoded for any media types.  Specific UI for particular media types will not be added unless there is a plugin for that specific media type.  "Watches" on the dashboard, for example, should be tied to visual media only.  TV, Movies, etc.  "Watches" does not apply to music.  "Listens" would be more appropriate.

---

## Completed (recent)

- Auth: global `AuthContext` — auth state initialized once at app root; login/register call `setUser` directly so navigation to `/` never flashes blank; `RequireAuth` handles loading/redirect; Layout no longer needs auth checks

- FileScanner confidence scores: Title(Year)=85, dotted=70, fallback=50
- Default scan threshold lowered 80 → 70
- ExternalIds added to MediaItemDto and all DTO constructors
- LibraryPage: clickable cards linking to detail page
- LibraryPage: media type badge overlaid on poster
- LibraryPage: broken image `onError` fallback
- MediaDetailPage: external IDs / metadata source chips
- MediaDetailPage: broken image `onError` fallback
- LibraryPage: status filter, sort (9 options), per-section paging (6/24/100/all), Save as Preset, Manage Presets, Reset
- LibraryPage: TMDB rating badge (★) on cards
- Add Media: TMDB scraper-backed search with media type selector pills (Movies, TV Shows, Music + dynamic from API)
- MediaDetailPage: TMDB metadata box always shown (not gated on existing external ID)
- MediaDetailPage: Refresh works for items with no external ID — auto-searches TMDB by name, stores found ID, then fetches full metadata
- MediaDetailPage: Refresh immediately updates poster and metadata in UI (`setQueryData`); also invalidates library so source list reflects new poster
- MediaDetailPage: prev/next navigation bar when accessed from a list (library passes sorted+filtered item IDs as router state)
- LibraryPage: card links pass sorted list + label as router nav state for prev/next traversal
- Layout: loading guard — no longer redirects to /login during initial auth check (prevents redirect loop)
- Background metadata refresh v2: `MetadataRefreshService` runs every 4h (configurable via `app_settings`), cycles all library root items × all active metadata plugins, writes per-plugin timestamps to `media_item_refresh_log`, surfaces last-refresh date in each provider's metadata box; `GET /api/v1/settings/app` + `PUT /api/v1/settings/app/{key}` (Admin) expose the interval setting
- Background metadata refresh: `MetadataRefreshService` refreshes all library root items on a 24h staleness cycle; new items (`MetadataRefreshedAt = null`) processed first; 500ms delay between API calls; 30s startup delay
- `MetadataRefreshedAt` column added to `media_items` (EF migration + model)
- LibraryPage: fold-scoped prev/next — navigating to detail page passes only the IDs visible in the current fold (not the full list)
- MediaDetailPage: hierarchical Up button — navigates to parent item (`↑ Up`) or back to library at the card's position (`↑ Library`) with hash-scroll restoration
- MediaDetailPage: single-item delete with inline confirmation strip; navigates to `/library` on success
- LibraryPage: multi-select batch delete — select mode with checkmark overlay, Select All, Delete (N) toolbar button, modal confirmation
- `scripts/RunTestEnvironment.ps1` — dev startup script (was `dev.ps1` at repo root); launches API on :8080 and Web on :3000 in separate windows
- Dark Teal theme — dark teal backgrounds, white primary text, neon green (#00ff88) accent; added to Preferences theme picker
- Lists: click-through to metadata — already implemented via `<Link to="/media/{id}">` on each row
- Lists: editable list name — already implemented (click title to rename inline)
- General UI: broken image `onError` fallback — all img tags across all pages now covered (ListDetailPage, MediaDetailPage child grid)
