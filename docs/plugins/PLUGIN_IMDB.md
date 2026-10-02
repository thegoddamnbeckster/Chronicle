# Chronicle.Plugin.IMDb — Design Document

**Plugin ID:** `chronicle.plugin.imdb`
**Version:** 1.0.0 (design rev. 2 — supersedes the RapidAPI-based scaffold design)
**Kind:** Full metadata provider (TMDB-style), plus scores for the generic ratings model
**Media Types:** `movies`, `tv` (Show / Season / Episode), `anime`, `anime_movies`, `fanedits` (source film), `music_videos` (new), `game` ("Video Games", shared with the game plugins), `people` (contributor). See §5.3
**Auth:** None
**Data source:** IMDb Non-Commercial Datasets — `https://datasets.imdbws.com/` (refreshed daily)
**Research verified live:** 2026-10-01

---

## 1. Data access: what is allowed

| Route | Allowed for Chronicle? | Notes |
|-------|------------------------|-------|
| **IMDb Non-Commercial Datasets** (TSV dumps) | ✅ **Yes** | Licensed "for personal and non-commercial use"; "You can hold local copies of this data". Requires the attribution line below |
| Reading imdb.com pages | ❌ No | IMDb Conditions of Use: "You may not use data mining, robots, screen scraping, or similar data gathering and extraction tools on this site" |
| Third-party "IMDb APIs" (RapidAPI wrappers, imdb-api clones) | ❌ No | They get their data by reading imdb.com, so using them has the same problem. The previous design for this plugin relied on one of these |
| Official IMDb API (GraphQL, via AWS Data Exchange) | ⚠️ Commercial only | Has everything (plots, images, release dates, certificates), but it's a paid business subscription |

IMDb's non-commercial terms add two rules this design has to follow:

1. *"If the information/data you want is not present in our datasets, it means it's not
   available for non-commercial usage."* Anything not in the TSVs is off-limits, full stop.
2. *"You must acknowledge the source of the data by including the following statement:
   Information courtesy of IMDb (https://www.imdb.com). Used with permission."* The plugin
   declares this through the manifest `attribution` field (ratings design §4), and the UI shows it
   wherever IMDb data is displayed.

IMDb "reserves the right to withdraw permission to use the data at any time", so the plugin
should degrade cleanly (keep the last good index) rather than assume the files will always be
there.

---

## 2. What the datasets contain

| File | Size (gz, 2026-10-01) | Rows | Content |
|------|----------------------|------|---------|
| `title.basics` | 228 MB | 12.8 M | `tconst`, `titleType`, `primaryTitle`, `originalTitle`, `isAdult`, `startYear`, `endYear`, `runtimeMinutes`, `genres` (≤3) |
| `title.akas` | 516 MB | 59.5 M | Localized/alternate titles with region, language, type (`dvd`, `festival`, `tv`, `working`, `original`, …) |
| `title.crew` | 83 MB | 12.8 M | Full director and writer lists per title |
| `title.episode` | 55 MB | 9.9 M | Episode `tconst` → series `tconst`, season #, episode # |
| `title.principals` | 785 MB | 102 M | Principal cast/crew: person, category (actor, actress, self, director, writer, producer, composer, cinematographer, editor, production_designer, casting_director, archive_footage …), job, **character names**, billing order |
| `title.ratings` | 8.7 MB | 1.7 M | `averageRating`, `numVotes` for every rated title, **episodes included** |
| `name.basics` | 311 MB | 15.7 M | `nconst`, name, birth year, death year, top-3 professions, known-for titles |

Title types in `title.basics`: tvEpisode 9.92 M, short 1.16 M, movie 758 K, video 332 K, tvSeries
306 K, tvMovie 156 K, tvMiniSeries 73 K, tvSpecial 61 K, videoGame 50 K, tvShort 11 K. 417 K
rows are flagged adult.

Spot checks: The Matrix `tt0133093` 8.7 (2,282,340 votes); Breaking Bad `tt0903747` 9.5
(2,683,831); "Ozymandias" `tt2301451` 9.5 (509,006).

---

## 3. Every IMDb datum and where it lives in Chronicle

Rule: everything IMDb provides becomes a Chronicle field, so it is stored, goes through the
user's precedence settings, and is visible. Where Chronicle has no field for it yet, the plugin
declares one (`docs/plans/2026-10-01-plugin-declared-fields-design.md`). The plugin offers its
values; **which provider wins any field is always the user's Metadata Assignment order**, with
no IMDb-specific exceptions.

| IMDb data | Chronicle field | Exists today? |
|-----------|-----------------|---------------|
| `tconst` / `nconst` | `media_external_ids` (`imdb`) | ✅ |
| `primaryTitle` | `title` | ✅ |
| `originalTitle` | `original_title` | ➕ new (declared by plugin) |
| `title.akas` (title, region, language, types, attributes, is-original) | `alternate_titles` (`structured`), also fed to search aliases | ➕ new |
| `startYear` | `year` | ✅ |
| `endYear` (series) | `end_year` | ➕ new |
| `runtimeMinutes` | `runtime_minutes` | ✅ |
| `genres` | `genres` | ✅ |
| `titleType` (movie / tvMovie / video / short / tvSpecial / tvMiniSeries / tvSeries / tvEpisode) | `title_format` | ➕ new |
| `isAdult` | `is_adult` (filterable) | ➕ new |
| principals: actor / actress | `cast` with character name(s) and billing order | ✅ (multiple characters: core change, one credit per character) |
| principals: self, archive_footage | `cast` with roles `Self`, `Archive Footage` | ➕ new roles |
| principals: other categories + `job` | `crew` with job | ✅ |
| `title.crew` directors, writers | `crew` | ✅ |
| `title.episode` parent / season / episode | hierarchy + item `Number` | ✅ |
| `averageRating` + `numVotes` | `rating.imdb` (ratings design) and, if the user ranks IMDb first, the ★ `rating` | ➕ new (ratings model) |
| Season score (vote-weighted mean of its episodes, computed by Chronicle) | `rating.imdb.episodes-avg` | ➕ new |
| Season episode count / year span | `episode_count`, `year`, `end_year` | ➕ `episode_count` new |
| `name.basics` birth / death year | `birth_date` / `death_date` with **year precision** | ✅ field; ➕ precision is new |
| `primaryProfession` | `professions` (`text_list`) | ➕ new |
| `knownForTitles` | `known_for` (`title_ref_list`, links to library items when present) | ➕ new |
| Person filmography (principals + crew by person) | `GetPersonCreditsAsync` → existing person-credits storage | ✅ |

**Not in IMDb's datasets, so not offered:** plot summaries, images, exact release and air
dates, content ratings, companies, countries, original language, keywords, budget, box office,
awards, Top 250 rank, trailers, biographies. Other providers (TMDB, TheTVDB, Fanart.tv) supply
those, and the user's precedence decides how they combine with IMDb's fields.


---

## 4. Architecture

The datasets total ~2 GB compressed and ~210 M rows, so the plugin works from a **local
index**, the same pattern `Chronicle.Plugin.MoviesRemastered` already uses (custom
`IPluginTask` + the plugin's persistent `__data_dir`). Once the index exists, lookups are
local and instant, with no per-item network calls and no rate limits.

```
Chronicle.Plugin.IMDb/
├── ImdbMetadataProvider.cs      # IMetadataProvider: search/scoring, GetById, episodes, person credits
├── ImdbSyncDatasetsTask.cs      # IPluginTask "sync-imdb-datasets"
├── Index/
│   ├── ImdbIndexBuilder.cs      # stream .tsv.gz → new SQLite file, then atomic swap
│   ├── ImdbIndexReader.cs       # read-only queries used by the provider
│   └── ImdbSchema.sql
├── Models/
└── tests/  (fixture TSV slices + scoring tests)
```

### 4.1 Index (`{__data_dir}/imdb.db`, SQLite, read-only to the provider)

- Integer keys: `tt0133093` → 133093, `nm0000206` → 206. Text IDs are rebuilt on output.
- Tables: `titles`, `akas` (filtered, below), `episodes`, `ratings`, `principals`, `crew`,
  `names`, `categories` (lookup), and an FTS5 table over primary/original/aka titles for search.
- Indexes: titles by id; episodes by `(series, season, episode)`; principals by title **and by
  person** (for filmographies); FTS for search.

### 4.2 Sync task

- **`sync-imdb-datasets`** (custom task, opt-in on first install like MRDb's):
  1. Download changed files only (HTTP `Last-Modified`), streaming to disk.
  2. Build a **new** `imdb.new.db` with one bulk-insert transaction per table
     (journal off, sync off, indexes created *after* the load).
  3. Swap it in atomically (`imdb.db` → `imdb.old.db`, `imdb.new.db` → `imdb.db`). A failed or
     interrupted build never touches the working index.
  4. Delete the downloaded `.gz` files unless `keep_downloads` is on.
- Default schedule: full rebuild **weekly**; ratings-only refresh (the 8.7 MB file) **daily**,
  which updates scores in place.
- After a sync, queue a lightweight "refresh IMDb partitions" pass so library items pick up the
  new ratings and votes. That pass is local reads only, so even 25,000 items is quick.

### 4.3 Size, scope & the disk-space warning

Rough estimate for a full index with integer keys: **5–9 GB on disk**, plus ~2 GB of
downloads while a sync runs. Principals (102 M rows) and akas (59.5 M) are most of it. A first
build is likely tens of minutes. The spike measures the real numbers, and the warning text
below is updated with them.

**Users are warned before they commit to it, in three places:**

1. **Top of the plugin's settings page:** a `SettingType.Notice` callout (severity `warning`):
   > **Disk space:** this plugin downloads IMDb's datasets (~2 GB) and builds a local index of
   > roughly 5–9 GB in the plugin's data folder. The first build can take 30+ minutes. Use the
   > scope settings below to make it smaller.
2. **The sync task's run confirmation** (manifest `run_confirmation`), with the same numbers.
3. **Each scope setting's description** says what it saves.

Scope settings:

| Setting | Default | Effect |
|---------|---------|--------|
| `include_adult` | on | Off drops 417 K adult titles and their credits |
| `title_types` | all | Drop IMDb title types you don't track (e.g. video games, shorts) |
| `aka_regions` / `aka_languages` | all | Restrict alternate titles to chosen regions/languages (the biggest single saving) |
| `episode_credits` | on | Off = episodes keep titles, numbers, runtime and ratings but no guest cast (principals is mostly episode rows) |

Defaults keep everything (nothing dropped); the settings are there for users short on space.


---

## 5. IDs & matching

### 5.1 External IDs

| Level | ExternalId | Example |
|-------|-----------|---------|
| Movie / show | `tt…` | `tt0133093` |
| Season | `{showTconst}/season:{N}` (IMDb has no season entity) | `tt0903747/season:5` |
| Episode | episode's own `tt…`, resolved from show + season + episode via `title.episode` | `tt2301451` |
| Person | `nm…` | `nm0000206` |

`GetAcceptedCrossRefPrefixes()` → `["imdb:"]`. Seasons and episodes are derived from the show,
never searched.

### 5.2 Search (`SearchAsync`): the shared scoring method

IMDb uses **the same cascade and scoring as TMDB, TVMaze and TheTVDB**
(`TmdbMetadataProvider.SearchAsync` / `ScoreCandidate` is the reference). The only difference
is where candidates come from: a local FTS query against the index instead of an HTTP search
endpoint. Scores stay directly comparable with every other plugin's, and Chronicle's acceptance
threshold (`DefaultConfidenceThreshold = 50`) applies unchanged.

**Stage 0: known ID.** `KnownExternalIds["imdb"]` present → `GetByIdAsync`, score 100,
reason `"cross-reference ID match"`. Most movies and shows already have one from TMDB (§8).

**Candidate source.** Titles to try = `AltTitles` (deduplicated, case-insensitive), falling back
to `[Name]`, with any residual `(YYYY)` suffix stripped exactly as TMDB does. Each title is
looked up in the FTS index over `primaryTitle`, `originalTitle` and akas, restricted to the
IMDb title types for `MediaTypeName` (§5.3), and returns the top matches by `numVotes`.

**Stage 1a: with year.** For each title in order, query with `startYear = Year` and score every
candidate. If any candidate scores ≥ 60 (`ExactMatchThreshold`), stop and return.

**Stage 1b: without year.** Otherwise query each title again without the year filter. Return
Stage 1a candidates first, then Stage 1b.

**`ScoreCandidate`** (identical rules and points):

| Signal | Points |
|--------|--------|
| Normalized title equal (`Normalize`: strip `: - , . '`, collapse spaces, lowercase) | +60, reason `title exact` |
| Normalized title contains / contained by query | +30, `title contains` |
| Year equal | +20, `year exact` |
| Year ±1 | +10, `year ±1` |
| Year differs by more | −10, `year mismatch` |
| `PreciseName` equal (case-insensitive, punctuation kept) | +15, `precise name exact` |
| `PreciseName` contains / contained by | +5, `precise name contains` |

The candidate's comparable title is `primaryTitle`. If the query matches only `originalTitle`
or an aka, the matched string is scored instead, so a library item named by its original or
localized title still scores as exact.

**Ordering:** by score, then by `numVotes` (IMDb's equivalent of TMDB's `popularity`
tiebreaker), top 10.

Any scoring improvement (cast overlap, runtime, format agreement) belongs in the shared method
for all plugins at once, not in IMDb alone, so plugins keep scoring the same way.

**People:** ID-based only (people-section design rule), via `nm` IDs taken from IMDb's own
credits on matched titles. No name search, same as TMDB.

### 5.3 Media types

IMDb's datasets contain exactly 11 title types (checked across all 12.8 M rows), plus people.
Counts exclude adult titles unless noted.

| IMDb type | Count (all) | What it is | Chronicle media type |
|-----------|-------------|-----------|----------------------|
| `movie` | 758 K | Feature films (11.5 K animated) | `movies`; `anime_movies` when that's the item's library type |
| `tvMovie` | 156 K | Made-for-TV films | `movies` |
| `short` | 1.16 M | Short films | `movies` (Chronicle's "Movies" type covers short films) |
| `tvShort` | 11 K | Short-form TV pieces | `movies` |
| `video` | 332 K | Direct-to-video releases, **except** music videos (below) | `movies` |
| `video` with genre *Music* | 28.8 K | **Music videos** | **`music_videos`**: new media type the plugin registers |
| `tvSpecial` | 61 K | One-off specials: stand-up (8.6 K comedy), award shows, concerts, holiday specials | `movies` (watched as a single item) |
| `tvSeries` | 306 K | Series (19.5 K animated) | `tv`; `anime` when that's the item's library type |
| `tvMiniSeries` | 73 K | Limited series | `tv` / `anime` |
| `tvEpisode` | 9.92 M | Episodes, linked to their series | Episode level of `tv` / `anime` (seasons derived from episode numbers) |
| `tvPilot` | 1 | Unaired pilot | `tv` |
| `videoGame` | 50 K | Video games | **`game`** ("Video Games"): the same type IGDB, LaunchBox, RAWG and Steam declare; whichever loads first registers it |
| (people) `name.basics` | 15.7 M | Actors, directors, writers, crew | `people` (contributor; ID-based only) |
| `fanedits` | n/a | IMDb has no fan edits | `fanedits` searches IMDb as movies, the same as TMDB, to pick up the source film's data |

**New media types.** The plugin declares them through `GetSupportedMediaTypes()` with a
`DisplayName`, which is how plugins already add media types to Chronicle:

| Name | Display | Levels | Verb / progress | IMDb fields offered |
|------|---------|--------|-----------------|---------------------|
| `music_videos` | Music Videos | 1 (flat, like Kodi's music-video library) | watched / minutes | title, year, runtime, genres, cast & crew, rating, plus a new `artist` field from the principal performer(s) |
| `game` | Video Games | 1 | played / percent | title, year, genres, voice cast & crew, rating. No platform (IMDb doesn't record it); see §5.4 |

Both types exist for anyone who tracks these media, whether or not a given library has any.
They're also open to other providers: a games plugin (IGDB, RAWG, Steam) can supply the same
type, and the user's precedence decides which provider wins each field. If such a plugin
registers the type first, IMDb only contributes to it.

**How the type mapping is used:**
- **Search:** the title types searched for an item come from its media type (above), so a
  movie query never matches a same-named series and vice versa (the same restriction TMDB applies).
  For `music_videos` the filter is `video` + genre *Music*; for `movies` it's every film-like
  type except music videos.
- **Stored:** the exact IMDb type is kept in the `title_format` field, so "TV movie", "short",
  "special" and "direct-to-video" stay distinguishable inside `movies` and are filterable.
- **Not split further:** talk, reality, news and game shows (70 K series, 3.4 M episodes) are
  ordinary `tvSeries` in IMDb. They stay in `tv`, identifiable by genre.

**To verify in the spike:** how reliably music videos credit the performing artist (often as
`self`), since that decides whether `artist` can be filled automatically or only by precedence
from another provider.

### 5.4 Video games & ROM collections

A ROM collection is a games library like any other, so `game` items come from three places:
manual Add Media, imports, and **scanning ROM folders** (§11 C7). IMDb is one of the providers
that can enrich them.

**What IMDb can and can't do for games:** it has 50 K `videoGame` titles with title, year,
genres, voice cast, crew and rating. It has **no platform**, no art and no description, and it
lists a game once even when it shipped on several platforms. So IMDb supplies what it has, and
dedicated game providers (IGDB, LaunchBox, RAWG, Steam) supply platform, art, descriptions and
release data. As always, the user's precedence settings decide how the two combine.

**Matching a ROM against IMDb:** the scanner hands IMDb a cleaned title (No-Intro / Redump /
TOSEC tags such as `(USA)`, `(Rev 1)`, `[!]`, `(Disc 2)` stripped; §11 C7) and the year if the
filename has one. IMDb then runs the normal shared cascade (§5.2), restricted to `videoGame`.
The same title on different platforms matches the same IMDb title, which is correct: platform
lives in its own field, not in IMDb's ID.

**Fields this adds to the registry:** `platform` (text, filterable; from the scanner or a game
provider, never from IMDb) and `region` (text_list, from ROM filename tags). Files of the same
game on different platforms are separate `game` items (each is its own thing to play and
track), and they link as versions of one work through the existing `media_groups` mechanism.

**Not in IMDb's scope:** checksum-based ROM identification (No-Intro/Redump DAT hashes). That's
the job of a dedicated game plugin and is noted here only so nobody expects it from IMDb.

---

## 6. Fields per level

### Movies / show (`GetByIdAsync("tt…")`)

| Chronicle field | Source |
|-----------------|--------|
| `Title` | `primaryTitle` |
| `Year` | `startYear` |
| `RuntimeMinutes` | `runtimeMinutes` |
| `Genres` | `genres` |
| `original_title`, `alternate_titles` | `originalTitle`; akas with region/language/types (also copied to `AlternateNames` for search) |
| `Cast` | principals `actor`/`actress`/`self` ordered by billing, with character names; `ExternalPersonId = "imdb:nm…"` |
| `Crew` | `title.crew` directors + writers, plus non-acting principals with their job |
| `Ratings` | `[{ Key: "imdb", Value: averageRating, Scale: 10, Votes: numVotes, Url: "https://www.imdb.com/title/tt…/" }]` |
| `end_year`, `title_format`, `is_adult` | `endYear`, `titleType`, `isAdult` (via `MediaMetadata.Fields`) |
| `ExtendedData` | `ids.imdb`, raw principals rows (category, job, characters, ordering), `ratingFetchedAt`. Everything here is also exposed through a declared field above; ExtendedData is the lossless raw copy |

No `Overview`, `PosterUrl` or `BackdropUrl`: not in the datasets (§3).

### Season (`tt…/season:N`)

Episode count, year range (first and last episode year) and a **computed season score**: the
vote-weighted mean of its episodes' ratings, with total votes. Stored with `Variant = "episodes-avg"`
so it's clearly Chronicle's calculation, not an IMDb number. IMDb doesn't publish season scores.

### Episode

Title, season/episode number, year, runtime, rating + votes, cast/crew (if `episode_credits`).
`GetEpisodeListAsync` returns the full season list (number + title), which makes IMDb usable as
a fallback episode guide for Kodi. It can't supply air dates, overviews or stills, so TMDB/TVDB
remain the preferred guide.

### People (contributor)

`birth_date`/`death_date` (year only, stored as year precision), professions → `Tags`,
known-for titles in `ExtendedData`, and `GetPersonCreditsAsync` with the **complete** IMDb
filmography: every principal and crew credit, with role and character, from the by-person index.

---

## 7. Manifest

```json
{
  "plugin_id": "chronicle.plugin.imdb",
  "name": "IMDb",
  "entry_type": "Chronicle.Plugin.IMDb.ImdbMetadataProvider",
  "brandColorLight": "#F5C518",
  "brandColorDark": "#F5C518",
  "fixMatchHint": "Enter an IMDb URL or ID (e.g. https://www.imdb.com/title/tt0133093/ or tt0133093)",
  "rating_sources": [
    { "key": "imdb", "display_name": "IMDb", "scale": 10, "kodi_name": "imdb",
      "url_template": "https://www.imdb.com/title/{id}/",
      "attribution": "Information courtesy of IMDb (https://www.imdb.com). Used with permission." }
  ],
  "metadata_fields": [
    { "key": "original_title",   "display_name": "Original Title",   "value_type": "text",           "blob_keys": ["originalTitle"],  "group": "Titles",         "sortable": true, "filterable": true, "kodi_label": "originaltitle" },
    { "key": "alternate_titles", "display_name": "Alternate Titles", "value_type": "structured",     "blob_keys": ["alternateTitles"], "group": "Titles" },
    { "key": "end_year",         "display_name": "End Year",         "value_type": "integer",        "blob_keys": ["endYear"],        "group": "Release",        "sortable": true, "filterable": true },
    { "key": "title_format",     "display_name": "Format",           "value_type": "text",           "blob_keys": ["titleFormat"],    "group": "Classification", "filterable": true },
    { "key": "is_adult",         "display_name": "Adult",            "value_type": "boolean",        "blob_keys": ["isAdult"],        "group": "Classification", "filterable": true },
    { "key": "episode_count",    "display_name": "Episodes",         "value_type": "integer",        "blob_keys": ["episodeCount"],   "group": "Release",        "sortable": true },
    { "key": "unplaced_episodes", "display_name": "Unnumbered Episodes", "value_type": "structured",  "blob_keys": ["unplacedEpisodes"], "group": "Release" },
    { "key": "professions",      "display_name": "Professions",      "value_type": "text_list",      "blob_keys": ["professions"],    "group": "People",         "filterable": true },
    { "key": "known_for",        "display_name": "Known For",        "value_type": "title_ref_list", "blob_keys": ["knownFor"],       "group": "People" },
    { "key": "artist",           "display_name": "Artist",           "value_type": "person_list",    "blob_keys": ["artist"],         "group": "People",         "sortable": true, "filterable": true, "kodi_label": "artist" }
  ],
  "background_tasks": [
    { "task_id": "sync-imdb-datasets", "display_name": "Sync IMDb Datasets",
      "default_cron": "0 3 * * 1", "default_enabled": false,
      "run_confirmation": { "title": "Download IMDb datasets?",
        "message": "Downloads ~2 GB from IMDb and builds a local index of roughly 5-9 GB in the plugin's data folder. The first build can take 30+ minutes; later runs only rebuild when IMDb has published new files. See the plugin settings to reduce the index size." } },
    { "task_id": "refresh-imdb-ratings", "display_name": "Refresh IMDb Ratings",
      "default_cron": "0 4 * * *", "default_enabled": true },
    { "task_id": "fetch-missing-metadata", "display_name": "Fetch Missing Metadata" },
    { "task_id": "resync-all-metadata", "display_name": "Re-sync All Metadata" }
  ]
}
```

`rating.imdb` and `rating.imdb.episodes-avg` are registered from `rating_sources` (ratings
design §3b), so they are not repeated in `metadata_fields`.

The settings schema opens with the disk-space `Notice` from §4.3.

Linking to IMDb title pages (`url_template`) is ordinary linking for the user to click, not
automated access.

---

## 8. Chronicle-side work this depends on or exposes

1. **Field registry:** `docs/plans/2026-10-01-plugin-declared-fields-design.md`. Needed so
   IMDb's new fields (original title, alternate titles, format, end year, professions, …) go
   through precedence and get shown, instead of sitting unused in the plugin's stored data.
   It also covers year-precision birth/death dates, multi-character credits, the `Self` /
   `Archive Footage` roles and `SettingType.Notice`.
2. **Ratings model:** `docs/plans/2026-10-01-ratings-tracking-design.md` (contract,
   `media_ratings`, scores as precedence-governed fields, manifest `rating_sources` with
   `attribution`, UI strip, Kodi).
3. **IMDb IDs that never reached `media_external_ids`:** on the live library, 3,392 movies
   carry an IMDb ID inside TMDB's stored data, but only 2,423 have an `imdb` row in
   `media_external_ids` (shows: 507 vs 379). `KnownExternalIds` is built from that table,
   so ~1,100 items would fall back to title search despite having an exact ID. Find why the
   cross-ref extraction misses them and backfill the rows. Episodes and seasons have none, which
   is fine: they're derived from the show.
4. **Attribution display** (ratings design §4/§6) is a licence requirement, not a nice-to-have.
   It has to ship with the plugin.
5. **Media type changes C1–C3** (§11): type capabilities in the type definition, labels from
   the type, credit fixes.
6. **Genre alias map** (§10.7) and **TMDB passing on people's IMDb IDs** (§10.6).

---

## 9. Decisions

| Question | Decision (2026-10-01) |
|----------|-----------------------|
| Disk use | Acceptable. Default to the full index (nothing dropped); warn users on the settings page, in the sync confirmation and in each scope setting (§4.3) |
| Display title (`primaryTitle` vs a localized aka) | Precedence. IMDb offers `title` (= `primaryTitle`), `original_title` and `alternate_titles`; the user's Metadata Assignment order decides what Chronicle shows |
| IMDb's score as the default ★ rating | Precedence. `rating` and `rating.imdb` are ordinary fields in Metadata Assignment |
| Data Chronicle has no field for | Declared as new fields (§3, field registry design), never left unexposed |
| Video games, music videos | Real media types (`game`, `music_videos`), supported whether or not a library has any. Games include ROM collections (§5.4) |
| Search scoring | The shared cascade and `ScoreCandidate` points used by TMDB/TVMaze/TheTVDB, unchanged (§5.2) |
| Games type name | `game`, displayed "Video Games", shared with IGDB/LaunchBox/RAWG/Steam so Chronicle has one games type |
| Adult titles | Indexed by default and flagged `is_adult`; hidden with a library filter, not by dropping data |

---

## 10. Edge cases & lifecycle

Gaps found when reviewing this design for completeness (2026-10-01).

### 10.1 Episodes IMDb can't place

**2,082,987 of 9,920,990 episodes (21 %) have no season or episode number** in
`title.episode`, and IMDb has no season 0 (specials are numbered inside normal seasons, or not
at all). Rules:

- Unnumbered episodes are never given invented numbers and never attached to a Chronicle
  episode by position.
- A Chronicle episode whose number has no IMDb match is matched by **exact normalized title**
  against the show's unnumbered episodes (shared `Normalize`), scored with the usual cascade.
  Below threshold, it's left unmatched for IMDb (other providers still enrich it).
- All of the show's unnumbered episodes are kept in the show's data as `unplaced_episodes`
  (declared field, `structured`), so nothing IMDb knows about is dropped.

### 10.2 Numbering that disagrees with other providers

IMDb's season/episode numbering can differ from TMDB/TheTVDB (split or merged seasons, anime
absolute numbering, double episodes). Before attaching episode data, the plugin compares IMDb's
per-season episode counts and titles with the children Chronicle already has (`ChildCount` /
`ChildNames`).

- Counts and titles agree: attach by number.
- They disagree: match by title only (as in 10.1) and record a `numbering_mismatch` diagnostic
  on the season, so it shows in the Enrichment drill-down instead of silently attaching wrong data.

### 10.3 Titles that vanish from the dataset

IMDb merges and deletes titles, and the dumps carry no redirects. If an item's `tconst` is
absent after a rebuild, its last IMDb data is **kept**, the enrichment row gets an
`imdb-missing` diagnostic, and the item is listed in the drill-down for a Fix Match. Nothing is
wiped because a dump lost a row.

### 10.4 Index lifecycle

- **No index yet** (fresh install): `SearchAsync`/`GetByIdAsync` return nothing,
  `HealthCheckAsync` returns false, and the plugin page says "Run *Sync IMDb Datasets* first"
  (same pattern as MRDb's search index).
- **Schema version** stamped in the index. A plugin update with a new schema triggers a rebuild
  instead of reading an incompatible file.
- **One sync at a time** (lock file in `__data_dir`). Readers keep using the old file until the
  swap, then reopen.
- **Disk full / download failure:** the build aborts, the working index stays in place, and the
  task reports the error.
- **After a rebuild,** only items whose IMDb data actually changed (rating, votes, title, credits
  hash) are re-resolved, not the whole library.

### 10.5 Fix Match input

`fixMatchHint` accepts `tt…`, `nm…`, `https://www.imdb.com/title/tt…/`,
`https://m.imdb.com/title/tt…/` and `imdb.com/name/nm…`, ignoring query strings. An episode
item accepts an episode `tt…` directly; its show and numbers are looked up in `title.episode`.

### 10.6 People cross-references

TMDB's person records carry an IMDb `nm` ID, but the TMDB plugin doesn't pass it on today. Once
it does (`ids.imdb` on people), IMDb's person data attaches to existing people by ID, with no
name matching. Until then, IMDb people attach only through IMDb's own credits on matched titles.

### 10.7 Genres

IMDb uses its own 28 genres (`Sci-Fi`, `Film-Noir`, `Reality-TV`, `Talk-Show`, `Game-Show`,
`Adult`, `Short`, …), which don't match TMDB's names (`Science Fiction`, …). Genre comes from
whichever provider wins precedence, so filtering a library by genre would split across names.
Needed: a DB-configurable genre alias map (same pattern as `metadata_field_aliases`), applied
when genres are resolved and seeded with the obvious pairs. IMDb's `Adult` and `Short` genres
are kept as genres *and* reflected in `is_adult` / `title_format`.

### 10.8 Adult titles

Indexed by default (nothing dropped) and flagged `is_adult`. The library gets an `is_adult`
filter (field registry, filterable), so users who don't want them shown can hide them without
losing data.

### 10.9 Duplicates

Two Chronicle items resolving to the same `tconst` are a duplicate signal. The existing
duplicate-candidate scan already pairs items that share an external ID, so `imdb` IDs join it
automatically once they're in `media_external_ids` (§8 item 3).

### 10.10 Search index details

FTS5 with the `unicode61 remove_diacritics 2` tokenizer, so non-Latin and accented titles match.
The index stores titles pre-normalized with the **same** `Normalize` the scoring uses, so search
and scoring never disagree about what "equal" means.

### 10.11 Release & deployment

- Built and tested like the other plugins; added to `scripts/RunTestEnvironment.ps1`'s plugin
  list and `PluginCatalogSeeds`.
- Released as a GitHub release asset on `Chronicle.Plugin.IMDb`.
- Tests: fixture TSV slices (unnumbered episodes, adult rows, music videos, games,
  multi-character credits), shared-scoring parity tests against TMDB's `ScoreCandidate`, and an
  index build/swap test.
- Own log file (standard per-plugin logging). The sync logs file sizes, row counts and build
  time, which also gives the real disk numbers for §4.3.

---

## 11. Required Chronicle changes for media types

From the code audit of every place a media type surfaces (2026-10-01). These are part of this
design, not optional follow-ups. All are generic: no change names a specific type.

**Already works for any plugin-declared type, no change:** type registration
(`PluginHostService.SyncMediaTypesFromPluginsAsync`), `GET /media/types` and every type picker
built from it (Add Media, Add Collection, media detail "Change Type", scan folders), Add Media
provider search (`ProvidersForType`), library grouping, global search, reports by type,
Metadata Assignment (`BuildAssignableFieldsAsync`), and per-plugin enrichment queues.

### C1. Media type capabilities move into the type definition

`MediaTypeSupport` (plugin side) and `media_types` (DB) gain:

| Property | Type | Replaces |
|----------|------|----------|
| `ProviderFamily` | string, e.g. `movie`, `tv`, `music` | Every substring switch that guesses a provider family from the type name: `FileScanService.ToMediaTypeHint`, the `MetadataEnrichmentService` cross-ref `typeHint`, `IsRootLevelIdTypeValid`. Today these send anything containing "music" (so `music_videos`) to music providers. Seed values keep today's rules: anime → tv, anime_movies → movie, fanedits → movie |
| `ClientTypeAliases` | string[], e.g. `["musicvideo"]` | The hardcoded scrobble type table in `MediaItemMatcher` (`"movie" or "film" => "movies"`, …), seeded with today's entries |
| `SupportsCollections` | bool | Hardcoded `movies / fanedits / anime_movies` lists in `LibraryService` and `MovieCollectionService` (the DB column already exists, but nothing reads it) |
| `CastLabel` | string, optional, e.g. "Performers", "Band Members", "Narrators" | The hardcoded `castLabel` in `MediaDetailPage` |

Merging rules in `SyncMediaTypesFromPluginsAsync`: aliases are unioned across plugins, the first
non-empty `ProviderFamily` / `CastLabel` wins, and `SupportsCollections` is true if any plugin
says so. `GET /media/types` returns all of them.

### C2. UI labels come from the type, not its name

- Status wording (`getStatusLabel`): derived from the type's stored `InteractionVerb`
  (watched / listened / read / played) instead of matching names. Fixes `game` and any future type.
- Child list headings (`getChildrenLabel`): from the type's stored `HierarchyLabels`. This also
  fixes an **existing bug**: anime shows list their seasons as "Items".
- Cast heading: `CastLabel` (C1), default "Cast".

### C3. Credits

- `PersonFullCreditsService` passes each provider credit's own media type through (validated
  against `media_types`) instead of `tv ? "tv" : "movies"`, so music-video and game credits land
  under the right type on a person's page.
- New credit roles `Self` and `Archive Footage` count as on-screen (`ON_SCREEN_ROLES`), so
  documentary appearances and music-video performers aren't filed under crew.
- One credit row per character for multi-character performances (field registry design §6).

### C4. File scanner: music videos

`Chronicle.Plugin.FileScanner` declares `music_videos` (flat): video extensions, plus filename
parsing for `Artist - Title` and `Artist - Title (Year)`, with the artist going to the `artist` field.

### C5. Kodi scrobbler: music videos

`media_info.py` maps Kodi's `musicvideo` to `musicvideo` (instead of `unknown`) and scrobbles it.
The server resolves it through `ClientTypeAliases` (C1).

### C6. Kodi scraper: music videos

A new `xbmc.metadata.scraper.musicvideos` addon (a third addon in `Chronicle_Scraper`, same
structure as the movie and TV ones), backed by a `/api/v1/scraper/musicvideos` endpoint. Kodi has
no games library, so there is no Kodi work for games.

### C7. File scanner: ROMs → `game`

`Chronicle.Plugin.FileScanner` declares `game` (flat) and scans ROM folders:

- **Platform registry, not code:** a DB-backed table (seeded, editable in Settings) mapping file
  extensions and folder names to platforms, e.g. `.sfc/.smc` → SNES, `.nes` → NES, `.gba` → Game
  Boy Advance, `.n64/.z64/.v64` → Nintendo 64, `.md/.gen` → Mega Drive. A folder-name match
  (`SNES/`, `Nintendo - Super Nintendo Entertainment System/`) wins over the extension for
  ambiguous formats (`.iso`, `.chd`, `.bin`, `.cue`).
- **Archives:** a `.zip` / `.7z` holding a single ROM is treated as that ROM (the inner
  extension decides the platform).
- **Multi-file games:** `.cue`+`.bin`, `.gdi`, `.m3u` playlists and `(Disc N)` sets become **one**
  game with parts, the same way multi-part movies already are.
- **Filename cleaning:** No-Intro / Redump / TOSEC tags are parsed out of the title and kept:
  regions → `region`, `(Rev N)` / `(v1.1)` → version, and `[!]`, `[h]`, `(Beta)`, `(Proto)` → tags.
- Writes the `platform` and `region` fields (§5.4).

### C8. Order

C1–C3 land before or with the IMDb plugin. C4–C7 are separate repo changes (FileScanner, Kodi
addons) that can follow. Until then, music videos and games still work through Add Media,
search, enrichment and the web UI.
