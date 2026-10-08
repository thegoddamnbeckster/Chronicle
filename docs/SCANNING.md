# Scanning: related files and wrong-type detection

Two additions to the file scanner. Both are additive: with the defaults, a scan imports exactly what it did before.

## Related files

Subtitles, artwork, theme music, extras and booklets that sit beside a movie, show or album are not media items. The
grouping step already set them aside; it now also *remembers* them.

* **What counts as an extra** is data, not code: `scan.sidecar_extensions` and `scan.sidecar_folders` in `app_settings`
  (comma separated; empty or missing means the built-in lists). Edit them on Settings -> Library -> Scanning. A change
  takes effect within about a minute (the scanner reads them through `CachedAppSettings`).
* **Which item owns a file.** The deepest group whose folder contains the file (a season's poster goes to the season, the
  show's poster to the show). A loose file in the scan root is matched to the loose media file with the same name
  (`Heat.en.srt` -> `Heat.mkv`). Anything unmatched is left alone.
* **Storing them is opt-in.** The scan page shows "+N related files" per group and a checkbox, "Remember subtitles,
  artwork and extras with each item" (off by default). The nightly scan follows `scan.bundle_related_files`
  (`true`/`false`, default `false`). Stored in `media_item_related_files` (item, path, kind, size, first seen).
* **Nothing is ever deleted by a scan.** A file that is no longer found gets `MissingSince`; it is cleared if the file
  returns. Deleting an item deletes its rows.
* **Where you see them.** A collapsed "Related files" section on the item's page, grouped by kind; `GET
  /api/v1/media/{id}/related-files`.
* `.nfo` files are not extras that Chronicle reads or writes (see the Kodi addon notes); they are ignored as before.

## Wrong-type detection

A folder scanned as Movies that is really TV (or music scanned as movies) used to import as nonsense. Each media type can
now say what its files look like, in `media_types.ScanHintsJson`:

```json
{ "filePatterns": ["(?i)\\bS\\d{1,2}E\\d{1,3}\\b"], "folderPatterns": ["(?i)^season\\s*\\d+$"], "extensions": [".mkv", ".mp4"] }
```

* **Patterns are distinctive** (an `S01E02` file name, a `Season 1` folder); **extensions are broad** (every video is
  `.mkv`). Patterns are regular expressions run with the non-backtracking engine and a 250 ms limit, validated when saved.
* A group is flagged as type **T** when at least 60 % of its files match T's distinctive patterns while at most 20 %
  match the scanned type's, or at least 60 % match T's extensions while at most 20 % match the scanned type's *and* the
  scanned type lists extensions itself (so a TV show with plain file names is never mistaken for movies). Only types
  with hints take part; a type with none is never flagged and never suggested. Ties are left alone.
* The migration seeds hints for the built-in TV (episode names, season folders), Movies (video extensions) and Music
  (audio extensions) types. Edit them, or give your own types hints, on Settings -> Media Types ("What its files look like").
  Leaving the field out of an update keeps the hints; an empty value clears them.
* **Manual scan:** the group shows "looks like TV" with the reason on hover and, if you can scan that type, a "Switch to
  TV and rescan" button. Nothing is changed for you.
* **Nightly scan:** `scan.mismatch_action` = `flag` (default) holds those groups back and sends a `scan.review`
  notification to administrators linking to the scan page; `ignore` imports them anyway. There is deliberately no
  automatic "reclassify and import", since a wrong guess would file things in the wrong place without anyone noticing.

## Your own file types

The scanner only imports files whose type it recognises (the common video and audio formats); everything else is skipped
as junk. If you keep something it does not know, add it under Settings -> Library -> Scanning -> "Your own file types":
`scan.extra_video_extensions` and `scan.extra_audio_extensions` (comma separated, a leading dot optional, plain
extensions only). Audio types also get their track numbers read from file names. A change applies within about a minute.
The FileScanner plugin has the same two settings (extra video / audio file types) for its own flat scan.

## Music file names

For audio files without usable tags the scan reads the name: `01 - Title`, `01. Title`, `1-02 Title` (disc 1, track 2),
`Track 05 - Title` and `Artist - 01 - Title`. A bare number is only a track number when it is zero padded or followed by
a separator, so `99 Problems` stays whole. A `1-02`-style name only counts as disc and track when other files in the
same folder are named that way. Embedded tags still take priority.
