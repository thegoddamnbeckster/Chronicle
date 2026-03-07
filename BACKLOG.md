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

## Media Detail

- *(nothing pending beyond what was just shipped)*

---

## General UI

- **No broken images** — All image elements need `onError` fallback to the letter-placeholder. Applied to: LibraryPage ✓, MediaDetailPage ✓. Audit remaining pages.

---

## Completed (recent)

- FileScanner confidence scores: Title(Year)=85, dotted=70, fallback=50
- Default scan threshold lowered 80 → 70
- ExternalIds added to MediaItemDto and all DTO constructors
- LibraryPage: clickable cards linking to detail page
- LibraryPage: media type badge overlaid on poster
- LibraryPage: broken image `onError` fallback
- MediaDetailPage: external IDs / metadata source chips
- MediaDetailPage: broken image `onError` fallback
