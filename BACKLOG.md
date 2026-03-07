# Chronicle — Backlog

Items collected from dev sessions. Roughly priority-ordered within each section.

---

## File Scanner

- **Browse button** — Every path input in the UI needs a folder-picker button. No exceptions. No manual typing of paths.
- **Persistent scan folders** — Scan paths saved to DB/settings. Pre-populated on the scan page. Scanning re-runs automatically on a background schedule (configurable interval).
- **Background scanning** — Scans run silently in the background and notify the user when new items are found.
- **Scan progress feedback** — Show current folder being scanned in real time, not a frozen spinner.
- **Music support** — FileScanner plugin: add audio extensions (.mp3, .flac, .m4a, .ogg, .wav, .aac), music filename parsing (Artist - Album - Track, etc.), register "Music" as a supported media type.
- **Flexible pattern matching** — Handle messy/unorganized folder structures (e.g. `E:\Video Downloads\MCM Download Parser`). Smarter fallback when standard patterns fail.
- **Scan results: accept items** — From the scan results page, user can approve/reject individual detected items before they're imported.
- **Scan results: show media type** — Display the detected media type badge in scan results rows.
- **Scan results: type mismatch correction** — If the movie scanner detects something that looks like TV (S01E01, etc.), automatically re-classify and match against the correct type.
- **Confidence score info** — Show the scoring formula somewhere accessible (e.g. a small info popup on the scan page). Formula: NFO+ID=100, Title(Year) filename=85, NFO+title+year=85, dotted/spaced filename=70, title only=50.

---

## Library

- **Indent sub-items in tree** — When showing hierarchical media (show → season → episode), indent child items visually so the parent-child relationship is obvious.

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

---

## Media Detail

- **All file paths** — Display every file path associated with a media item in the metadata page, both internal (Chronicle's data store) and external (original path on disk). If multiple files exist (e.g. different cuts, multiple episodes), list them all.
- **Image thumbnails** — Show all available images (poster, backdrop, etc.) as actual thumbnails in the metadata page rather than links. Local images stored with the media should also be shown inline.

---

## Plugins

- **Plugin catalog from GitHub** — Move the plugin catalog out of the hard-coded server array and into a `plugins.json` file stored in the Chronicle GitHub repo. The server should fetch this file at startup (or on demand) so new plugins can be listed by editing the file, without a code deploy. Users should be able to browse the catalog and choose which plugins to install.

---

## General UI

- **No broken images** — All image elements need `onError` fallback to the letter-placeholder. Applied to: LibraryPage ✓, MediaDetailPage ✓. Audit remaining pages.

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
- Background metadata refresh: `MetadataRefreshService` refreshes all library root items on a 24h staleness cycle; new items (`MetadataRefreshedAt = null`) processed first; 500ms delay between API calls; 30s startup delay
- `MetadataRefreshedAt` column added to `media_items` (EF migration + model)
