# Chronicle — Backlog

Items here are captured and approved for future implementation but **not yet started**.
Pick up any item by moving it to an active sprint and implementing in order.

---

## 1. Steam Scrobbler

**Repo:** `W:\Scripts\Chronicle.Service.Scrobbler.Steam\` (new standalone repo)

Notify Chronicle when Steam launches or closes a game.

- Listens for Steam game launch and close events (via Steam API or process monitoring)
- Sends scrobble events to Chronicle on launch (start) and close (stop/complete)
- Progress tracking: duration played per session
- **Per-user tracking**: Player A and Player B have completely separate play totals
- Reports must support viewing all players individually and in aggregate
- Authenticated via Chronicle API key (one key per installation/user)
- Runs as a background service / system tray app on Windows

---

## 2. MusicBee Scrobbler

**Repo:** `W:\Scripts\Chronicle.Service.Scrobbler.MusicBee\` (new standalone repo)

Fire scrobble events to Chronicle when MusicBee plays media.

- Fires on **song start** AND on **song end**
- Tracks progress through the current track (position / duration)
- Covers all MusicBee-playable content: **songs, podcasts, audiobooks**
- Sends scrobble start/stop/progress events to Chronicle
- Uses a Chronicle API key for authentication
- Integrates with MusicBee's plugin system (C# plugin DLL loaded by MusicBee)

---

## 3. MusicBee Plugin

**Repo:** `W:\Scripts\Chronicle.Plugin.MusicBee\` (new standalone repo)

A Chronicle plugin that bridges MusicBee and Chronicle bidirectionally.

- **Metadata pull**: Downloads song / album / artist metadata from Chronicle into MusicBee
- **Scraping registration**: Registers with Chronicle as a metadata source (implements `IMetadataProvider`)
- **Scrobbling registration**: Registers with Chronicle for scrobble event handling
- Uses MusicBee's official plugin interface format (C# plugin DLL)
- Distinct from the Scrobbler above — this is the Chronicle-side plugin, the Scrobbler is the MusicBee-side service

---

## 4. Build Documentation

**Location:** `W:\Scripts\Chronicle\docs\BUILD.md` (or `INSTALL.md`)

Comprehensive build and install guide covering all scenarios:

- **Full build**: Build Chronicle + all first-party plugins + frontend
- **Plugin-only build**: Build a single plugin in isolation
- **Chronicle-only build**: Build core without any plugins
- **Easy Windows install**: Step-by-step installer guide (no dev tools required for end users)
- **Easy Docker install**: Docker Compose setup, volume mounts, port mapping, first-run config
- Should include all necessary artifacts and prerequisites for each build type
- Cover both developer and end-user perspectives

---

## 5. Media Identification Plugin

**Repo:** `W:\Scripts\Chronicle.Plugin.MediaIdentification\` or extend `Chronicle.Plugin.FileScanner`
(possibly a new service: `Chronicle.Service.Filescan.Identify`)

Identify unrecognized media files and prompt the user to confirm or correct metadata.

### Identification
- Reads file metadata: ID3 tags, video container metadata, filename, folder naming conventions
- Confidence scoring with a configurable threshold
- Falls back through: embedded tags → filename patterns → folder name patterns

### UI / UX
- Unidentified files listed grouped by folder
- Navigation menu badge: superscript on the relevant nav item — white text on red background — showing count of unidentified files
- Best-guess identification displayed for user review
- User can **approve** the guess or **correct** it (type/select the correct media item)
- If the corrected media is not yet in Chronicle, automatically trigger a metadata pull from the configured external metadata source (e.g., TMDB, MusicBrainz)

### Performance
- Efficient and low CPU — does not block normal Chronicle operation
- Runs as a background scan, not inline with file system events
- Configurable scan schedule or manual trigger

---

*Last updated: 2026-03-06*
