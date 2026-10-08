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

## Messy folders and download-style names

Tidy libraries (`Show (2008)/Season 1/...`, `Heat (1995)/...`) are read exactly as before. When the structure says
little, the scanner falls back to the *name* (`ReleaseNameParser`), reading what comes before the first episode code,
year or quality word:

* `Movie.Name.2019.1080p.BluRay.x264-GRP` (folder or file) becomes **Movie Name (2019)**.
* A release-named show folder (`Show.Name.S02.1080p.BluRay.x264-GRP`) is cleaned to **Show Name**; the real folder path
  stays the matching key, so rescans find the same item.
* Episodes sitting loose in the scan root (`Show.Name.S02E03.Episode.Title.720p.HDTV.x264-GRP.mkv`, `Show Name 1x05`,
  `[Group] Show Name - 1x05 [1080p]`) are filed under a show and season built from their names; before, they were left
  ungrouped. If the show also has a real folder, they join it.
* A name only counts as "a release" when it contains a resolution, source or codec word (1080p, BluRay, x264, ...).
  Titles that merely contain words like *Cam*, *Web*, *Dual*, *Internal* or *Complete* are left alone.
* **Everything worked out from a name alone is held below the automatic-import threshold** (movies 70 %, derived shows
  capped at 70 %): the scan page lists them for review, the nightly scan does not import them by itself.
* Names that give no show and episode numbering stay ungrouped and are not imported.

### Anime and daily-show numbering

Besides `S02E03` / `1x05`, two more numbering styles place a file in a series:

* **Running numbers** that continue across seasons: `[Group] Show Name - 112 [1080p][ABCD1234]`, `Show Name - 07 - Title`,
  `Show Name EP112`, `Show Name - 05v2`. The episode is filed under **Season 1** with the running number kept as its
  episode number ("Episode 112", or the title if the name has one). Chronicle does not know the show's real season
  boundaries, so it does not invent them; metadata providers that understand absolute order (TheTVDB, Simkl) can map the
  number when the item is enriched. A bare four-digit number that looks like a year is never an episode number
  (`Movie Name - 2019`), and neither is a resolution (`- 1080p`).
* **Air dates** for daily shows: `Show Name 2019-05-12`, `Show.Name.2019.05.12`. One season per year (`Season 2019`), the
  date as the episode name (`2019-05-12`), no episode number. Only year-first dates are read: `12-05-2019` is ambiguous.

Inside a show folder these files used to be skipped (no `S01E02` code); now they are filed under the folder's show. Loose
in the scan root they build a derived show, held for review as above. Audio files are never read this way. A numbered
extra inside a show folder (`Show - Behind the Scenes - 2.mkv`) can be read as an episode; put extras in an `Extras`
folder (see the extras list above) to keep them out.

## Renamed and moved files

Chronicle never renames or moves your files; it only records where they are. When a file is renamed or moved after a scan
already saw it (Sonarr or Radarr importing and renaming it, a manual tidy-up), the next scan follows it:

* Each scan records the file's size and modified time (its "fingerprint"; a rename or move keeps both).
* A new file is treated as an existing item's file when **every file that item had recorded is gone from disk** and either
  the fingerprint matches, or (for an episode or track) the item has the same season/episode number under the same parent.
  The item keeps its identity (metadata, watch history, ratings) and its paths are updated. Exactly one candidate must
  match; anything ambiguous is treated as a new item, as before.
* Two different items are never merged while their files both exist, however alike they look.
* Items scanned before this existed have no fingerprint yet; they gain one at their next scan. Episodes and tracks are
  covered immediately by the number rule.

## Detect the media type automatically

On the scan page, **Media type -> Detect automatically** sorts each file into the type its own name and format say it is,
instead of treating the whole folder as one type. A folder with movies, TV episodes and music comes out as groups of each
kind, each labelled with its type, and importing creates every group as its own type (one progress bar for the lot).

* **How a file is sorted** (`FileTypeClassifier`) uses the scan hints on Settings -> Media Types, nothing hard-coded:
  a file that matches a type's *distinctive* patterns (an episode code, a `Season N` folder) belongs to that type;
  otherwise it goes to the lowest-numbered type that lists its file extension; otherwise it has no type and is not offered.
  Subtitles, artwork and other extras go with the files around them. A type you create takes part as soon as it has hints.
* **Not picked automatically:** inactive types, types without hints, and types with a special scan style (audiobooks:
  choose it explicitly, since a folder of audio files is a music album or an audiobook and only you know which).
* Each type's files are grouped the way that type is normally grouped (episodes into shows and seasons, tracks into
  albums), including the download-name and anime-numbering rules above.
* The wrong-type warning is not shown in this mode (there is no single chosen type to be wrong about).
* **Not available for saved scan folders / the nightly scan yet**: a saved folder still has one type.
