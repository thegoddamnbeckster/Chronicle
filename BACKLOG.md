# Chronicle — Backlog

Items collected from dev sessions. Roughly priority-ordered within each section.

---

## File Scanner

- **Browse button** — Every path input in the UI needs a folder-picker button. No exceptions. No manual typing of paths.
- **Persistent scan folders / watched folders list** — Scan paths saved to DB/settings (e.g. `E:\Music`, `G:\Videos\Movies`, `J:\Videos\TV`). Pre-populated on the scan page. Each new folder the user scans is automatically added to the watched list. The background scan cycle re-walks every watched folder; any new files found above the detection confidence threshold are auto-imported into the library and immediately queued for metadata scraping. Removed files should be flagged (not silently deleted).
- **Media Type auto-detection** — FileScanner must determine the media type of each file on its own (extension + embedded tags + folder name heuristics). It should not rely on the user declaring which type of media is in a folder. Speed is secondary — this runs as a background process.
- **Background scanning** — Scans run silently in the background and notify the user when new items are found.
- **Scan progress feedback** — Show current folder being scanned in real time, not a frozen spinner.
- **Music support** — FileScanner plugin: add audio extensions (.mp3, .flac, .m4a, .ogg, .wav, .aac), music filename parsing (Artist - Album - Track, etc.), register "Music" as a supported media type.
- **Other file support** - FileScanner plugin: allow user to add their own filetypes.
- **Flexible pattern matching** — Handle messy/unorganized folder structures (e.g. `E:\Video Downloads\MCM Download Parser`). Smarter fallback when standard patterns fail.
- **Scan results: accept items** — From the scan results page, user can approve/reject individual detected items before they're imported.
- **Scan results: show media type** — Display the detected media type badge in scan results rows.
- **Scan results: type mismatch correction** — If the movie scanner detects something that looks like TV (S01E01, etc.), automatically re-classify and match against the correct type.
- **Confidence score info** — Show the scoring formula somewhere accessible (e.g. a small info popup on the scan page). Formula: NFO+ID=100, Title(Year) filename=85, NFO+title+year=85, dotted/spaced filename=70, title only=50.
- **Scan performance** — Scanning a folder should take only as long as it takes to enumerate filenames on disk. Metadata fetching (TMDB lookups, etc.) must move entirely to a background step so a large folder (e.g. H:\Movies with 500+ files) doesn't time out the scan request or cause a "Network error" in the UI.
- **Related files & NFO parsing** — The file scanner metadata box on the media detail page should list all related files found in the same folder (e.g. subtitle files, companion images). Picture files (.jpg/.png) in the folder should be selectable and attachable to the media item. NFO files in the same folder should be detected, parsed, and their fields displayed inside the file scanner metadata box.
- **Folder-relative file listing** — Show every file in the same folder as the scanned media item, directly inside the scanner metadata box on the detail page.

---

## Library

- **Indent sub-items in tree** — When showing hierarchical media (show → season → episode), indent child items visually so the parent-child relationship is obvious.
- **Content** - as media is added ot the library, the library should display it dynamically.
- **Metadata** - Metadata can be downloaded by any metadata plugin, not just TMDB.  Each set of metadata per media item shall be shown in it's own box labelled for that metadata provider (TMDB, Trakt, SIMKL, TinyMediaManager, LastFM, etc).  Each box will be labelled for the metadata provider's name and have the metadata provider's icon.
- **Prev/next navigation end-of-section behaviour** — When the user reaches the last item in a folded library section and presses Next (or first item and presses Prev), let the user choose the behaviour in Library Settings: (a) wrap back to the beginning of the same section (current default), (b) automatically continue into the next/previous folded section, or (c) grey out the button when there are no more items (clearest, safest).

---

## Lists

- **Click-through to metadata** — When choosing items from a list, clicking an item should navigate to the same media detail page shown from the library.
- **Editable list name** — Ability to rename a list inline or from a settings panel.

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
- **Image thumbnails** — Show all available images (poster, backdrop, etc.) as actual thumbnails in the metadata page rather than links. Local images stored with the media should also be shown inline. Clicking on the thumbnail should show the full size image in a new window.
- **Collections / sets / lists as clickable links** — On the media detail page, any collection, set, or curated list the item belongs to (e.g. TMDB `belongs_to_collection`) should appear as a clickable link underneath the title and above the delete button. The delete button should be right-aligned. Clicking the collection link navigates to a collection detail view that lists all member items.

---

## Metadata

- **Automatic background refresh from all sources** — The background metadata refresh service runs every 4 hours (configurable in Settings) and walks every library item. For each item it runs every active metadata plugin that is appropriate for that media type (e.g. TMDB for movies/TV, LastFM for music, Trakt/SIMKL for tracked media, FileScanner to detect file changes and new files in watched folders). All plugins run in the same cycle — no per-plugin scheduling. New files discovered by FileScanner that exceed the confidence threshold are auto-imported and immediately queued for the other metadata scrapers. Track the last successful refresh timestamp per item per plugin in a `media_item_refresh_log` table (`item_id`, `provider_name`, `refreshed_at`). Display these per-plugin timestamps in the metadata detail box for each provider on the media detail page. Show subtle progress feedback in the bottom-left corner of the UI only (not a modal or blocking spinner).
- **Metadata source API (Kodi / external callers)** — Expose a per-item metadata endpoint that returns all stored metadata fields, partitioned by source (TMDB, MusicBrainz, etc.). Allow the caller (e.g. a Kodi plugin) to query which Chronicle field maps to which Kodi metadata purpose (e.g. "poster", "fanart", "rating"). The mapping should be user-configurable in Settings so different providers can be prioritised per field per media type.

---

## Plugins

- **Plugin catalog as child page** — Move the Browse Catalog UI out of the Plugins page and into its own dedicated child page under Plugins in the sidebar. The catalog should be a first-class navigation destination.
- **Plugin catalog from GitHub** — Move the plugin catalog out of the hard-coded server array and into a `plugins.json` file stored in the Chronicle GitHub repo. The server should fetch this file at startup (or on demand) so new plugins can be listed by editing the file, without a code deploy. Users should be able to browse the catalog and choose which plugins to install.
- **Adding and removing** - the user should be able to add new plugins based on plugins.json from github and remove them from the local Chronicle app.  Chronicle should immediately reflect the changes and add or remove menu items and UI elements depending on the plugin.
- **plugin files** plugins are to be built in their own repos.  Finished files and their hashes are to be registered in plugins.json.
- **Security ** When downloading files from github, Chronicle will confirm that the hash at github matches the downloaded file's hash.  Downloaded files will be treated as hostile until they can be verified as safe - we must ensure that the user's computer is never compromised.  Security is paramount.  If this means scanning the file with an external security service, then this should be an option.  Either locally or online.
- **included plugins** - Filescanner must be included as part of the Chronicle install.  Filescanner will remain a separate project and repo, but the dll needs to be included with the main Chronicle installation.

---

## General UI

- **No broken images** — All image elements need `onError` fallback to the letter-placeholder. Applied to: LibraryPage ✓, MediaDetailPage ✓. Audit remaining pages.
- **Dev environment launch script** — Keep a script (e.g. `scripts/start-dev.ps1`) updated that always points to the active worktree and starts both the API (port 8080) and frontend (port 3000). User should be able to run one script to get the exact environment that was being worked on when a session ended.

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

## Planned

### Movie Collections
Group movies into named collections (e.g. "Alien Collection"). TMDB returns
`belongs_to_collection` on each movie response. Collections use `media_groups`
table. Each member movie links to the collection. Collections show their own
art, synopsis, and member list.
Touches: Chronicle.Plugin.TMDB, MetadataRefreshService, library UI.

### Dynamic Library Loading
Replace the single `getLibrary(undefined, 1, 500, true)` call with paginated/virtual
scroll. Count badges appear immediately; cards fill in progressively as the user
scrolls. Prevents page freeze on large libraries (1000+ items).

### FileScanner Audio Support Extensions
FileScanner v1.1.0 added basic audio file scanning (MP3, FLAC, OGG, etc.) and
embedded tag reading. Future improvements:
- Multi-disc album support (disc number grouping)
- Compilation album handling (Various Artists)
- Classical music metadata (composer, conductor fields from tags)
- Album art extraction and caching
- Podcast/audiobook file format support

---

## Completed (recent)

- File Scan: direct import — removed mandatory TMDB identification step; files are imported immediately from scanner data (title, year, file path); TMDB metadata enrichment happens automatically in the background via MetadataRefreshService; `POST /api/v1/scan/import-direct` endpoint added
- Plugin catalog: SHA-256 integrity verification on all catalog downloads — rejects ZIPs whose hash doesn't match the catalog entry; protects against compromised releases and MITM attacks
- Plugin catalog: GitHub API asset URL download (replaces `browser_download_url`) — authenticated via optional `GitHub:Token` config; works for public and private repos
- Plugin catalog: File Scanner plugin added — `chronicle.plugin.filescanner` available in Browse Catalog
- FileScanner v1.0.0 and TMDB v1.0.0 plugins released to GitHub with ZIP assets
- FileScanner README created

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
- Background metadata refresh: `MetadataRefreshService` refreshes all library root items on a 24h staleness cycle; new items (`MetadataRefreshedAt = null`) processed first; 500ms delay between API calls; 30s startup delay
- `MetadataRefreshedAt` column added to `media_items` (EF migration + model)
