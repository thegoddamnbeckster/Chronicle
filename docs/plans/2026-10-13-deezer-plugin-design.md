# Deezer plugin: design

Status: **design, awaiting decisions (section 13).** Repo: `Chronicle.Plugin.Deezer` (public, currently an unusable scaffold: every method throws `NotImplementedException` and the README links to an `example.com` placeholder; it is replaced, not extended). Plugin id `chronicle.plugin.deezer`, source name `deezer`.

## 1. Purpose and scope

A third metadata source for the `music` media type (Artist / Album / Track), beside MusicBrainz and Last.fm. No API key, no account.

Why it is worth building (measured on the live library, 2026-10-13):

* Music is the biggest type that has the fewest sources: 247 artists, 7,599 albums, 47,499 tracks. Movies and TV have six or more providers each; books now have two.
* MusicBrainz found nothing for **3,866 of 7,599 albums (51%)** and **15,256 of 47,499 tracks (32%)**. Last.fm found nothing for 3,574 albums and 7,697 tracks.
* A prototype run against the live Deezer on 22 albums MusicBrainz could not find: 14 found the right album, the rest are releases Deezer does not have (e.g. Ghost, "Prequelle"), box sets ("Prince of Darkness") or discography folders (see 5.2). Many of the "albums" in this library are really singles, remixes and live tracks (TKO, Magic, "Don't (Don Diablo remix)", "Sad Song (demo, 2024)"), and Deezer's catalogue is strong exactly there.
* Deezer gives what the other two do not: 1000x1000 covers, artist pictures, genres, ISRC and UPC codes, label, explicit flag, popularity. It has no biographies, so it supplements; it does not replace Last.fm's text.

In scope: all three levels. Out of scope, on purpose: playback and the 30-second previews (their URLs are signed and expire within hours: storing one would store a dead link), lyrics, playlists, charts, user data, any scraping.

## 2. What Deezer gives us (verified against the live service)

| Need | Endpoint | Notes |
|------|----------|-------|
| Find an album | `GET /search/album?q=<free text>&limit=` | Returns id, title, cover urls, genre id, track count, record type (`album`, `single`, `ep`, `compile`), explicit flag, artist (id, name, picture). |
| Find a track | `GET /search/track?q=<free text>&limit=` | id, title, `title_version`, duration, rank, explicit flag, artist, album (id, title, cover). |
| Find an artist | `GET /search/artist?q=<name>&limit=` | id, name, pictures, `nb_fan`, `nb_album`. |
| Full album | `GET /album/{id}` | adds `upc`, `label`, `release_date`, `genres.data[]` (names), `fans`, `duration`, `contributors`, and **the whole track list inline** with duration, rank, position. |
| Full track | `GET /track/{id}` | adds `isrc`, `track_position`, `disk_number`, `release_date`, `bpm`, `gain`, `contributors` (with roles), `available_countries`. |
| Full artist | `GET /artist/{id}` | name, pictures (up to 1000x1000), `nb_album`, `nb_fan`. |
| Exact lookup | `GET /track/isrc:{ISRC}`, `GET /album/upc:{UPC}` | Identifier matches (verified: both resolve). |

Usage: a documented quota of 50 requests per 5 seconds per IP, no authentication for these read endpoints. Measured: 70 sequential requests in 21 seconds (about 3.3 a second, bound by latency) with no quota response.

Findings that shape the design (each measured, several the opposite of what the documentation suggests):

1. **The advanced search syntax cannot be used.** `artist:"X" track:"Y"` returned zero results for every one of 16 sampled tracks, including "Rihanna / Pon de Replay" (which exists); `artist:"Journey"` returned "Arizona Journey" and no Journey. Plain free text (`Rihanna Pon de Replay`) finds both. Queries are therefore plain text and **all ranking is done by the plugin**.
2. **Free text is an AND of every word**, so a long query finds nothing: "Ghost Prequelle" has no result although "Prequelle" alone has some. The fallback is title alone, then filtering by artist ourselves.
3. **A third of every result page is junk:** "(Karaoke Version)" by La-Le-Lu, "Tribute", "8D Audio", "Nightcore", "Sped Up", "Techno", "Lullaby", "in the style of", ringtones, "Sing it Live", "Bardcore". Same title, wrong artist. Without an artist check these win ("It's My Life (Orchestral Version)" by Melody Orchestra for Bon Jovi).
4. **Duplicates are normal.** "Doubt" by Jesus Jones appears as an 11-track and a 21-track edition; "Heligoland" as 10 and 14; "Country Grammar" three ways (standard, deluxe, a bonus-track cut). The plugin must pick between editions, not just between artists.
5. **The artist field is the weak signal in this library.** Names carry folder noise ("Simon & Garfunkel discography", "Beastie Boys discography", "The Chemical Brothers discography") and broken characters ("B?la Fleck", "blink?182", "Don?t"): the library holds a replacement character where an accent or an apostrophe was. Both must be matched around, not through.
6. **Errors can arrive as HTTP 200.** Deezer's quota, missing-item and permission errors are a JSON body `{"error":{"code":N,...}}`. Code 4 is the quota, 800 is "no data". An unchecked 200 would read as "found, empty".
7. **Preview URLs are signed with an expiry** (`hdnea=exp=...`): not stored.
8. Cover and picture URLs on `cdn-images.dzcdn.net` are stable, size is a path segment (`1000x1000-000000-80-0-0.jpg`), and need no request to build.

## 3. Identity and IDs

* ExternalIds carry the kind, because Deezer's numeric ids overlap across kinds (artist 27 and album 27 are different things): `artist:27`, `album:302127`, `track:3135556`. Same convention MusicBrainz and Last.fm already use here. The parser also accepts `deezer:` + id, deezer.com URLs (`/album/302127`, with or without a language segment), and `isrc:`/`upc:` codes.
* **Fix match** accepts: a kind-prefixed id, a deezer.com URL, `isrc:GBDUW0000059` (track), `upc:724384960650` (album). A bare number is refused with a message naming the forms above, because it would be ambiguous.
* `GetAcceptedCrossRefPrefixes()` = `deezer:`.
* ExtendedData `ids` (read by the host to seed other plugins): `isrc` for tracks, `upc` for albums. Put under `extendedData.external_ids` too so nothing is lost. (Neither MusicBrainz nor Last.fm accepts these as seeds today, so this is inert but correct; the host decides.)
* Host rule to live with: an external id belongs to one Chronicle item library-wide. Where the library holds the same album twice, the second cannot take it. That is a duplicate to clean up, and it shows in the Enrichment items list as "already belongs to a different item", as it does for Open Library.
* **The enrichment row remembers the matched id** and reuses it on the next run instead of searching (the host bug found and fixed on 2026-10-13 while correcting Open Library matches). Nothing for the plugin to do, but the tests must show that clearing a match and re-running performs a real search.

## 4. Types, levels and priority

* Declares `music`, `HierarchyLevels = 3`, labels `Artist, Album, Track`, verb `listened`: identical to Last.fm's declaration, so type creation stays consistent.
* `LevelFields`: level 0 `poster_url`; level 1 `poster_url`, `genres`, `year`; level 2 `runtime_minutes`, `genres`, `year`, `poster_url`, `cast`. Deezer has no overview, rating or tags at any level, and declaring fields it never fills would put rows in a queue that can only fail (the empty-fields lesson in reverse).
* `DefaultPriority`: see decision 1. Proposed **30**: MusicBrainz (10) keeps identity and Last.fm (20) keeps text, and Metadata Assignment can move Deezer first for covers, which is where it is better.
* `EnrichOnly = false`: it takes part in search so Add Media can find a release MusicBrainz lacks.

## 5. Matching

One normaliser, the Open Library one: fold accents, lower-case, drop a leading article, drop everything that is not a letter or digit. Two additions for music: **a broken character** (`?`, U+FFFD) in a library name matches any one character of the Deezer name (so "B?la Fleck" meets "Béla Fleck" and "Don?t" meets "Don't"), and names lose a trailing "discography", "greatest hits", "collection" and "(YYYY)" before use.

### 5.1 Artist (level 0)

1. Known `deezer` id -> fetch (100, identifier match).
2. `search/artist?q=<name>&limit=10` (plain text). Keep candidates whose name equals the item's after normalising. Never match a similar name ("Dream Theater" returned "Pan Brownie's Dream Soundtrack").
3. Score: 60 base, plus up to 20 for popularity (`log2(nb_fan)`, so Journey with 663,757 fans beats "Journey" with 23), plus 10 when the library's albums for this artist appear in the candidate's catalogue (children confirmation, 5.4).
4. No equal name -> no result.

### 5.2 Album (level 1)

1. Known id -> fetch. Known `upc` -> `album/upc:`.
2. Query 1: `search/album?q=<artist> <title>`. If it returns nothing, query 2: title alone (limit 25), kept only where the artist agrees. At most two searches plus one `album/{id}` for the winner.
3. Title forms: full, and with edition qualifiers removed ("(Deluxe Edition)", "(Remastered 2015)", "(Expanded)", "- Single", "(Bonus Track Version)"). A **version qualifier that changes the recording must agree on both sides**: "(Don Diablo Remix)", "(Live ...)", "(Demo)", "(Acoustic)", "(Instrumental)", "(Radio Edit)", "(Karaoke)". "TKO" is not "TKO (Radio Edit)" unless the library says so; the library's own qualifier is kept.
4. Score:
   * title equal 60; equal after removing edition qualifiers 55; one title continuing the other 40; **never "appears inside"** (the Open Library v1.0.2 lesson: "Galaxy's Edge" is not "Legionnaire (Galaxy's Edge)");
   * artist equal +30. **A known artist that does not match caps the score at 35**, below the host's gate of 50: this removes every karaoke, tribute and cover version in one rule;
   * junk: titles or artists matching the junk list (karaoke, tribute, "in the style of", 8D, nightcore, sped up, slowed, reverb, lullaby, ringtone, "made famous by", bardcore, "originally performed", workout, "cover version") -40 unless the library item itself says so;
   * `record_type`: `compile` and "Various Artists" -15 unless the library artist is Various Artists;
   * year within 1 of the library year +8 (re-releases are common, so small);
   * **edition choice:** among candidates with the same title and artist, the one whose track count equals the library's child count (`ChildCount`) +15, and one whose track list contains the library's track names (`ChildNames`) +up to 20. This is the Open Library "children" lesson applied: the 11-track and 21-track "Doubt" are told apart by what the library actually holds;
   * more fans: up to +6, a tiebreak only.
5. Return the top five; the host applies its gate (50).

The library's "album" is often a single named after its song, or a folder named for a box set ("Prince of Darkness", "Bridge Over Troubled Water" under "Simon & Garfunkel discography"). The artist normaliser handles the second; the first needs nothing special because Deezer lists singles as albums of `record_type: single`.

### 5.3 Track (level 2)

The expensive level (47,499 items), so it is built to cost almost nothing:

1. **From the album the item sits on.** The album lookup of 5.2 runs once per album and is cached for ten minutes (the enrichment pass handles the tracks of an album together). Its `album/{id}` response already lists every track with title, duration, position and rank. A track is matched **inside that list**: title equal 60, duration within the tolerance (default 5 s, a setting) +25 (30 s with no duration known: 0), position equal to the library's `ItemNumber` +10, edition qualifiers ignored. **Zero requests per track** after the album is resolved.
2. If the album cannot be resolved, or the title is not in its list: `search/track?q=<artist> <title>`, scored the same way against the artist (cap 35 on mismatch), the junk list, and the library's album name (+15 when the result's album equals it). One request.
3. `isrc` known (a library file tag or a sibling's `SubItemMetadata` tag) -> `track/isrc:` -> identifier match (100).
4. Compilation tracks ("Various Artists" albums) match on the track's own artist, not the album's.

### 5.4 Confirming by what the library holds

Open Library taught that a name alone cannot tell two real candidates apart; the library's own children can. Level 0 compares the library's album titles with the candidate artist's `artist/{id}/albums`, at most one extra request and only when two candidates score within 10 of each other. Level 1 compares track names and counts (above), at no extra cost because the album response already carries the list.

## 6. Field mapping

| Chronicle field | Source |
|-----------------|--------|
| Title | artist name / album title / track `title` with `title_version` appended in brackets when Deezer separates it |
| AlternateNames | the library's wording of a matched title or artist (so the host's name gate accepts "Simon & Garfunkel discography"); `title_short`; the plain title without its edition qualifier |
| Year | album `release_date` year; track `release_date` year |
| PosterUrl | album `cover_xl` (size setting); artist `picture_xl`; track: its album's cover |
| Genres | `album.genres.data[].name` (album); a track inherits its album's genres |
| RuntimeMinutes | track `duration` seconds rounded to minutes (exact seconds in extendedData) |
| Cast | artist and contributors as `CastMember(name, role, "artist:<id>", picture)`: roles `Main`, `Featured`, `Remixer`, from `contributors[].role` |
| ExtendedData | `ids` (section 3), `external_ids`, album: `upc`, `label`, `record_type`, `nb_tracks`, `duration_seconds`, `fans`, `release_date`, `explicit_lyrics`, `explicit_content_lyrics`, `available`, the Deezer link; track: `isrc`, `duration_seconds`, `track_position`, `disk_number`, `rank`, `bpm`, `gain`, `explicit_lyrics`, `release_date`, `available_countries_count`; artist: `nb_fan`, `nb_album`, link |

Nothing invented: no overview, no rating (`rank` and `fans` are popularity, not a score, so they stay in extendedData), no tags. **Not stored:** the preview URL (expires), the `track_token` (a session token).

## 7. Settings

| Key | Default | Meaning |
|-----|---------|---------|
| `cover_size` | `xl` (1000x1000) | `xl`, `big` (500), `medium` (250). Chosen by the user, not hard-coded; the URL is built from the path segment. |
| `duration_tolerance_seconds` | 5 | How far a library track's length may differ from Deezer's and still count as the same recording. |
| `junk_terms` | the list in 5.2, one per line | Words that mark a karaoke, tribute or cover release. Editable, because new kinds appear (this is the rule a user will want to tune, and it must not need a release). |

No contact email and no key: Deezer asks for neither. User-Agent: `Chronicle-Deezer/<version>`.

## 8. Rate limiting, errors, caching

* One limiter per plugin instance: **at least 150 ms between requests** (about 6 a second, comfortably inside 50 per 5 seconds, and measured throughput is lower than that anyway).
* Budget per item: artist at most 2, album at most 3 (two searches and the detail), track 0 after its album, 1 without. Chronicle gives a plugin 25 seconds per call (4 during a file scan), so nothing may exceed 4 requests.
* **Quota is thrown, not waited out:** the 200-body `code 4` and any HTTP 429 become `HttpRequestException` with status 429, so Chronicle leaves the item Pending and stops the pass instead of marking it not found (the AniList lesson). A `Retry-After` of 8 seconds or less is waited once.
* Other `error` bodies: code 800 ("no data") and code 300 (not found) are a normal "not found". Anything else is an error that propagates, so Chronicle retries it.
* 5xx and network errors propagate.
* Ten-minute in-memory cache of fetched albums (with their track lists), artists and tracks.
* Never download images in bulk: URLs only, the app fetches when asked.

Budget for the first full pass over this library: 247 artists x 1.5 + 7,599 albums x 2.6 + 47,499 tracks x about 0.15 (most hit the album cache) = about **27,000 requests, 2.5 to 3 hours at the measured 3.3 a second**, against about 100,000 for a design that searched each track. Re-sync is off by default.

## 9. Background tasks

`fetch-missing-metadata` (daily, enabled) and `resync-all-metadata` (weekly, disabled), the host's built-in handlers, cron offset from MusicBrainz and Last.fm.

## 10. Evidence from the prototype

Sample: 44 library albums (22 MusicBrainz "not found", 22 found), 16 tracks, 14 artists.

* "Not found" albums: Deezer found the exact album for Tracy Chapman, Nine Inch Nails, Bela Fleck (both, despite the broken character), B.B. King, Herbie Hancock, Jesus Jones, Incubus, Rush, Skrillex, Ed Sheeran, Avenged Sevenfold, Oasis (a 2024 demo), Fort Minor, The Crystal Method. Not found, correctly or for a known reason: the artist was a "discography" folder (Simon & Garfunkel, Beastie Boys, Chemical Brothers: fixed by stripping the word), Ghost "Prequelle" (not on Deezer at all), Ozzy Osbourne "Prince of Darkness" (a box set). Found only by the title-alone fallback: Daft Punk "Aerodynamic" (a 2-track single).
* Wrong without the rules in 5: Bon Jovi's "It's My Life" returned orchestral and techno versions by other artists first; Phil Collins "One More Night" returned two other artists' versions; Paramore's "Still Into You" returned a covers compilation. Each is removed by the artist cap, and the "compile" penalty handles the compilations.
* Tracks: the advanced-syntax query failed all 16; plain text found them, which is why section 5 relies on plain text only.
* Artists: 14 sampled; the right artist is the top result for plain text in every case and ranks by `nb_fan` where namesakes exist.

## 11. Tests

* Unit, with recorded real responses as fixtures (including the errors that arrive as HTTP 200): normaliser, broken-character matching, folder-noise stripping, edition and version qualifiers, junk detection (every prototype case above), artist scoring (namesakes, popularity), album scoring (edition choice by child count and names, singles, compilations, "appears inside" rejected), track matching inside an album list (duration, position, qualifiers), id/URL/ISRC/UPC parsing, cover size URL building, field mapping.
* Contract tests with a fake HTTP handler: request budget per call (never more than 4), a track costs 0 after its album and 1 without, the 200-body quota error becomes a 429, "no data" is empty, pacing, the cache stops repeat fetches, `Retry-After` handling, no preview URL or token ever stored.
* Host test (Chronicle repo): clearing a Deezer match and re-running searches again (regression for the stale enrichment id).
* Live tests, off unless `DEEZER_LIVE=1`: the library's real artists, albums and tracks, pacing respected.
* Manifest test: id, version, tasks, supported types and levels match the code.

## 12. Release and docs

README in the standard plugin format (badge, Plugin ID / Version / Auth block, supported types with fields, ID formats, how matching works, settings, tasks, structure, building), `plugins.json` entry (tags `music`, `metadata`), a GitHub release with one zip (DLL, deps.json, manifest), and the two places that list plugins by hand: `Deploy-Plugin.ps1` (it refused Open Library for exactly this reason) and `RunTestEnvironment.ps1`. Host changes: none. The first deploy needs the plugin's file hash approved on the Plugins page, and Background Tasks now says so when it is waiting.

## 13. Decisions needed

1. **Priority:** 30 (after Last.fm; Deezer fills gaps, you move it up per field for covers), or 15 (Deezer's covers and genres win over MusicBrainz's by default).
2. **Scope:** all three levels (recommended: tracks cost almost nothing thanks to the album cache and they are where the gap is), or albums and artists first.
3. **Track genres:** a track inherits its album's genres (recommended), or only albums carry genres.
4. **Cover size default:** 1000x1000 (recommended for a TV) or 500x500.
5. **Junk terms** as an editable setting with the default list in 5.2 (recommended), or fixed in the plugin.
6. **Library names with "discography":** the plugin strips the word when matching (recommended), or leaves those artists unmatched and waits for you to rename the folders.

## 14. Risks

* **Terms of use.** Deezer's public API is documented for non-commercial applications; this is a self-hosted personal library, which fits. If Deezer restricts or removes anonymous access, items stay Pending and nothing breaks.
* **Regional catalogue.** Deezer's catalogue varies by country and some artists are absent entirely (Ghost). Expect a ceiling below 100% on albums; "not found" is the honest answer.
* **Junk that is not in the list.** New kinds of bulk-uploaded cover releases appear; the artist cap catches the common case and `junk_terms` is editable without a release.
* **Wrong edition.** Mitigated by child count and names; where the library holds neither, the more popular edition wins, and Fix match corrects it.
* **First pass is long** (about 3 hours), by design and in the background.
