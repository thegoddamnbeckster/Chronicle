# Chronicle_Scrobbler (Kodi Addon) — Design

**Date:** 2026-07-26
**Status:** Approved — Phase 1 scope confirmed, later phases roadmapped

---

## Overview

`Chronicle_Scrobbler` is a Kodi addon, modeled directly on `W:\Scripts\SIMKL_Scrobbler`
(the mature, production `script.simkl` addon — see its own
`simklscrobbler_projectplan.md` for the full reference feature set), retargeted at
Chronicle instead of SIMKL. Unlike `docs/FEATURE_KODI_PLUGIN.md`'s superseded
server-push design, this is a **Kodi-side addon**: it runs inside Kodi, calls out to
Chronicle's REST API, and is **bidirectional** — the same relationship SIMKL_Scrobbler
has with simkl.com.

Goal (user's own words): "I want Chronicle to be able to provide any data that can be
sent to Kodi and retrieved from Kodi. This includes things like watch counts, ratings.
Anything."

### Consolidating prior stub attempts

Two near-identical, near-empty skeleton repos already existed for this exact concept —
`service.chronicle.scrobbler` and `Chronicle.Service.Scrobbler.Kodi` (diffed
byte-for-byte identical except the addon-id string). Their `lib/` scaffold is real,
working code — not boilerplate — and is the starting point here:

| Reused from stub | Status |
|---|---|
| `lib/logger.py` | Reused as-is |
| `lib/chronicle_client.py` | Reused, extended with library/rating/history methods |
| `lib/media_info.py` | Reused as-is (Kodi JSON-RPC wrapper + playback snapshot) |
| `lib/monitor.py` | Reused as-is (playback lifecycle → scrobble triggers) |
| `lib/progress_tracker.py` | Reused as-is (scrobble-timing decision rules) |
| `lib/device_auth.py` + `lib/qr_dialog.py` | Reused as-is (QR device-auth flow) |
| `lib/reset_manager.py` | Reused as-is (Kodi-side progress reset, no Chronicle round-trip) |
| `lib/playlist_sync.py` | Reused as-is (Chronicle Lists → Kodi .m3u) |
| `default.py` / `service.py` | Reused, menu extended for new Phase 1 actions |

Both stub repos are deleted once these pieces land in `Chronicle_Scrobbler` (per
explicit instruction — neither was serving a different purpose; confirmed identical).

---

## Full roadmap (mirrors SIMKL_Scrobbler's own phase history)

| Phase | Scope | This design doc covers |
|---|---|---|
| **1** | Auth, live scrobbling, bidirectional watch-history + rating sync (the data-parity core) | **Yes — full detail below** |
| 2 | Branded rating-dialog UI (prompt after watched-threshold, star/number picker) | Roadmapped only |
| 3 | Context-menu addons (rate / toggle-watched / manual-sync as separate `context.chronicle.*` addons — Kodi requires these standalone) | Roadmapped only |
| 4 | Exclusion settings (Live TV, HTTP streams, plugin sources, custom paths) | Roadmapped only |
| 5 | Incremental sync via activity timestamps, scheduled auto-sync interval | Roadmapped only |

Phases 2–5 are deferred by explicit user decision — build the core bidirectional
data-parity engine first, confirm it works end-to-end, then layer polish on top.

---

## Phase 1 scope

### 1. Authentication (reused)

QR device-auth flow against Chronicle's already-built `DeviceAuthController`:

```
POST   /api/v1/auth/device             → { code, displayCode, qrUrl, verificationUrl, expiresInSeconds }
GET    /api/v1/auth/device/{code}/poll → { status: pending|approved|denied|expired, apiKey }
GET    /api/v1/auth/device/{code}/qr   → QR PNG
```

No changes needed — the stub's `device_auth.py`/`qr_dialog.py` already implement this
exactly against the real, live Chronicle contract.

### 2. Live scrobbling (reused)

`monitor.py` + `progress_tracker.py` + `media_info.py` already implement:
start/pause/resume/seek/stop detection → `POST /api/v1/scrobble` with progress,
current/total time, external IDs. Confirmed this matches `ScrobbleController.cs`
exactly (`ScrobbleRequestDto`: `mediaItemId`, `progressPercent`, `timestamp`,
`deviceName` — **note:** the stub's payload shape predates knowing the real DTO and
sends `title`/`year`/`externalIds` directly rather than a resolved `mediaItemId` — see
"API gaps to close" below).

### 3. Chronicle → Kodi sync: ratings, watch counts, last-played, art (NEW — the core of Phase 1)

This is the actual "provide any data... watch counts, ratings... anything" ask, and the
piece neither stub repo attempted. New module: `lib/sync_engine.py`.

**Two different reconciliation rules, by field type (both user decisions, 2026-07-26):**

- **Rating and art:** Chronicle is the explicit source of truth. Unconditional
  overwrite — whatever Chronicle has for a field, Kodi's local value is replaced with
  it, no gap-filling, no timestamp comparison. There's no meaningful "which side is
  more current" question for a single rating value.
- **Playcount / last-played:** genuine two-way reconciliation, since either side can
  legitimately have a watch the other doesn't know about (a watch on a different
  Chronicle-tracked source Kodi never saw, or vice versa — a Kodi watch before this
  addon existed / while offline). For each matched item, compare Kodi's `lastplayed`
  against Chronicle's most recent watch timestamp for that item (derived from
  `GET /api/v1/scrobble/history`, max `MarkedAsWatched=true` event timestamp):
  - **Kodi's `lastplayed` is newer** → Chronicle is missing watch(es) → submit enough
    synthetic scrobble events (`progressPercent: 100`) to Chronicle to bring its
    derived count up to match Kodi's `playcount`.
  - **Chronicle's last watch is newer** → Kodi is missing watch(es) → push Kodi's
    `playcount`/`lastplayed` up to match Chronicle's derived count.
  - **Equal / no data on one side** → no-op for that item.

```
GET /api/v1/library?status=Completed&page=N&perPage=100   (paginate through all)
GET /api/v1/library?status=Watching&page=N&perPage=100
GET /api/v1/library?status=Dropped&page=N&perPage=100
GET /api/v1/library?status=PlanToWatch&page=N&perPage=100
```
For each `LibraryEntryDto`, match against Kodi's library (ID-priority chain: IMDB →
TMDB/TVDB uniqueid → title+year — factored into `lib/kodi_matcher.py`, shared with
`playlist_sync.py`), then unconditionally push:
```
VideoLibrary.SetMovieDetails / SetEpisodeDetails
  {
    movieid,
    userrating: <UserRating>,               // whenever Chronicle has one
    playcount:  <derived watch count>,       // see below
    lastplayed: <derived from scrobble history>,
    art: { poster, fanart, banner, clearlogo, clearart, discart, ... }  // whenever Chronicle has a value for that slot
  }
```

**Watch counts specifically:** Chronicle has no dedicated per-item playcount field —
`LibraryEntryDto` tracks status/rating/notes/timestamps, not a numeric watch count.
"Times watched" is derived by counting `MarkedAsWatched=true` events for that
`mediaItemId` from `GET /api/v1/scrobble/history` (paginated). This matches how Kodi
itself derives `playcount` — an event count, not a stored counter — and requires no new
Chronicle server API.

**Images specifically:** Chronicle's resolved `MediaMetadata` fields (`PosterUrl`,
`BackdropUrl`→`fanart`, `LogoUrl`→`clearlogo`, `BannerUrl`→`banner`, `ClearartUrl`,
`DiscUrl`) map onto Kodi's `art` dict almost 1:1. Pushed unconditionally like every
other field, per the source-of-truth decision above — confirmed live against the
user's real Kodi 21+ library that `SetMovieDetails`/`SetEpisodeDetails` accept an
`art` object directly.

**Matching:** identical ID-priority chain to `playlist_sync.py`'s existing
`_find_movie_path`/`_find_episode_path` (IMDB → TMDB/TVDB uniqueid → title+year),
factored out into a shared `lib/kodi_matcher.py` so both `playlist_sync.py` and the
new `sync_engine.py` use one matching implementation instead of two.

### 4. API gaps to close (Chronicle server-side, small additions)

Two small, additive changes needed to `Chronicle.API` (not full new features — filling
gaps this addon's bidirectional need exposes):

1. **`ScrobbleRequestDto` needs a match-by-external-id path.** Today it requires an
   already-resolved `mediaItemId` — the addon doesn't have one for an item Kodi knows
   about but Chronicle has never seen scrobbled before. Add an optional
   `externalIds: Dictionary<string,string>` + `title`/`year`/`mediaType` fallback to
   `ScrobbleRequestDto`, and have `ScrobbleService` resolve-or-create the
   `MediaItem`/`UserLibrary` row when `mediaItemId` is absent — mirroring how
   `Chronicle.Plugin.Trakt`/`Chronicle.Plugin.Simkl` already resolve incoming import
   items by external ID.
2. **No bulk "my library, all statuses, with external IDs" endpoint exists in one
   call.** `GET /api/v1/library` already supports paging + status filter; the addon
   Phase 1 sync just calls it once per status (`Completed`, `Watching`, `Dropped`,
   `PlanToWatch`) rather than requiring a new endpoint. No server change needed here
   after all — confirmed sufficient.

Only gap 1 requires a Chronicle server change; it's scoped as part of this same
implementation pass since the addon can't function without it.

### 5. Menu additions (`default.py`)

New entries alongside the stub's existing menu:
- **Sync Watch History & Ratings Now** — runs `sync_engine.sync_all()` both directions,
  progress dialog (mirrors `playlist_sync.py`'s existing progress-dialog pattern).

---

## Settings schema (Phase 1)

| Key | Type | Default | Notes |
|---|---|---|---|
| `chronicle_url` | Text | _(empty)_ | Chronicle server base URL |
| `api_key` | Text (hidden, set via device auth) | _(empty)_ | |
| `scrobble_movies` / `scrobble_tv` / `scrobble_music` | Bool | `true` | Reused from stub |
| `poll_interval` | Number | `30` | Reused from stub |
| `watched_threshold` | Number | `80` | Reused from stub |
| `sync_ratings` | Bool | `true` | New — Phase 1 |
| `sync_playcount` | Bool | `true` | New — Phase 1 |
| `sync_direction` | Dropdown: Both / Chronicle→Kodi only / Kodi→Chronicle only | `Both` | New — Phase 1 |

---

## Repository

**Name:** `Chronicle_Scrobbler` (per explicit naming choice — matches `SIMKL_Scrobbler`'s
own underscore convention as the repo/folder name; the Kodi addon id itself stays
dotted-lowercase, `service.chronicle.scrobbler`, per Kodi's own addon-id convention).

**Location:** `W:\Scripts\Chronicle_Scrobbler`
**GitHub:** `thegoddamnbeckster/Chronicle_Scrobbler` (new; supersedes and replaces
`service.chronicle.scrobbler` and `Chronicle.Service.Scrobbler.Kodi`, both deleted
after this lands).

```
Chronicle_Scrobbler/
├── addon.xml
├── default.py
├── service.py
├── README.md
├── LICENSE
├── lib/
│   ├── __init__.py
│   ├── logger.py                 (reused)
│   ├── chronicle_client.py       (reused + extended)
│   ├── media_info.py             (reused)
│   ├── monitor.py                (reused)
│   ├── progress_tracker.py       (reused)
│   ├── device_auth.py            (reused)
│   ├── qr_dialog.py              (reused)
│   ├── reset_manager.py          (reused)
│   ├── playlist_sync.py          (reused)
│   ├── kodi_matcher.py           (NEW — factored out of playlist_sync's matching logic)
│   └── sync_engine.py            (NEW — bidirectional watch-history + rating sync)
└── resources/
    ├── settings.xml
    └── language/resource.language.en_gb/strings.po
```

---

## Implementation order

1. Chronicle server: extend `ScrobbleRequestDto`/`ScrobbleService` to resolve-or-create
   by external ID (gap 1 above) — small, additive, own commit in `Chronicle.API`.
2. Scaffold `Chronicle_Scrobbler` repo — copy the 9 reused `lib/` files +
   `default.py`/`service.py`/`addon.xml`/`resources/` from the stub, rename addon id.
3. `lib/kodi_matcher.py` — factor matching logic out of `playlist_sync.py`.
4. `lib/sync_engine.py` — bidirectional sync (both directions, rating + derived
   playcount, last-write-wins conflict rule).
5. `lib/chronicle_client.py` — add `get_library(status)`, `update_library_entry(id,
   rating, status)`, extend `scrobble()` payload to support the external-ID fallback.
6. Wire "Sync Watch History & Ratings Now" into `default.py`'s menu.
7. `resources/settings.xml` — add the three new Phase 1 settings.
8. README, LICENSE, git init, GitHub repo + push.
9. Delete `service.chronicle.scrobbler` and `Chronicle.Service.Scrobbler.Kodi`.
10. Update `docs/FEATURE_KODI_PLUGIN.md` — done (superseded note added).
