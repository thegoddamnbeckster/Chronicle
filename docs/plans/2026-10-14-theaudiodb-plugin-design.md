# TheAudioDB plugin: design

Status: **implemented as v1.0.0 (2026-10-14).** Decisions taken: tracks included (see 6), the documented 30 requests a minute obeyed, enrich-only, priority 40, cut-out as character art, rating from 3 votes. Changes made while building are in section 18. Repo: `Chronicle.Plugin.TheAudioDB` (public, a stale scaffold: every method throws `NotImplementedException`, it is written against the old `SearchAsync(string)` interface, and its docs name a test key that no longer works; it is replaced, not extended). Plugin id `chronicle.plugin.theaudiodb`, source name `theaudiodb`.

Everything in sections 2, 5, 8 and 13 was measured against the live service and this library on 2026-10-14 (the recorded responses are in the plugin repo under `tests/fixtures/`). What could not be measured is marked **unverified**.

## 1. Purpose and scope

A fourth metadata source for the `music` media type, beside MusicBrainz, Last.fm and Deezer, and the first one whose main contribution is **artwork the library has none of**.

Measured on the live library (resolved fields, 2026-10-14):

* **Artists (247):** none has a thumb or clear art; 90 have no banner, 41 no logo, 38 no backdrop.
* **Albums (7,599):** none has disc art, a thumb or clear art; 1,952 have no poster, 6,486 no rating, 5,873 no genre.
* TheAudioDB fills exactly these slots (section 8). Sampled against the live service: artists with a thumb 38 of 38, logo 36, wide thumb 26, banner 27, clear art 23, cut-out 17, fan art 38; albums with CD art 31 of 43, 3D case 25, back cover 20, genre 33, description 29, score 15.
* It also carries a full artist biography (38 of 38 sampled), album descriptions (29 of 43) and album reviews (6 of 43), in 14 languages, plus mood, style, theme and speed.

Expected yield, from the samples in section 13: about 240 of 247 artists, about 3,500 of 7,599 albums (46%), about 2,500 disc images, about 235 artist thumbs and 145 clear art images.

**Scope: artists, albums and tracks** (section 6 says how tracks are made reliable on the free tier).

Out of scope, on purpose: lyrics (copyright), charts and trending, labels and venues, any scraping, the v2 API (premium only).

## 2. What TheAudioDB gives us (verified against the live service)

Base `https://www.theaudiodb.com/api/v1/json/{key}/`. Source of the limits below: the service's own documentation page (theaudiodb.com/free_music_api) and checked against it.

| Need | Endpoint | Notes |
|------|----------|-------|
| Artist by name | `search.php?s=<name>` | Free: **one** result. Premium: up to 10. |
| Artist by MusicBrainz id | `artist-mb.php?i=<artist mbid>` | Exact. |
| Artist by id | `artist.php?i=<id>` | Exact. |
| Album by artist and title | `searchalbum.php?s=<artist>&a=<title>` | One result. The title must be **exactly** the stored title (see 3.3). |
| Album by MusicBrainz id | `album-mb.php?i=<release-group mbid>` | Exact. The id is a **release group**, the same id MusicBrainz gives an album here. |
| Album by id | `album.php?m=<id>` | Exact. (`album.php?i=<artist id>` lists an artist's albums: **free returns one**.) |
| Track, track list, videos, charts | `track.php?h=<id>`, `searchtrack.php?s=<artist>&t=<title>`, `track-mb.php?i=<recording mbid>` | Section 6. (`track.php?m=<album>` lists an album's tracks: free returns one; `mvid.php`, charts: not used.) |

A search response already carries the **whole record**, so the lookup by id the host makes after a search can be answered from memory (section 10).

| | Free | Premium (US$8 a month) |
|---|---|---|
| Key | `123` (the key `2` in the scaffold and in the service's own examples now answers **404**) | the user's own key, same v1 URLs, no client change |
| Rate | **30 requests a minute**; a breach is HTTP 429 and "wait another minute" | 100 a minute |
| Lists | one result for every list and search endpoint | full lists, 10 search results |

Findings that shape the design (each measured):

1. **The free tier returns one result, never a ranking.** Searches for "Coldplay" albums, an album's tracks and an artist's albums all returned exactly one entry, whichever it was. The plugin therefore cannot choose between candidates: **its job is to verify the one it is given** (name, artist, year) and refuse it when it does not fit.
2. **Exact title only.** "Dark Side of the Moon" does not find "The Dark Side of the Moon"; "Abbey Road (Remastered)", "Nevermind (Deluxe)", "OK Computer OKNOTOK 1997 2017" find nothing. Artist search, in contrast, is forgiving: accents are folded ("Beyonce" finds Beyoncé), aliases work ("Pink" finds P!nk, "Beatles" finds The Beatles, "Simon and Garfunkel" finds Simon & Garfunkel), but not "Corrs" for The Corrs. The album search's artist argument is **not** forgiving ("Simon and Garfunkel" finds no album, "Simon & Garfunkel" does).
3. **Typographic characters break every search.** A hyphen U+2010 ("Deee‐Lite", "blink‐182") and curly quotes ("Guns N’ Roses", "Tale Spinnin’") find nothing; their ASCII forms do. 354 of this library's 7,846 artist and album names contain one.
4. **Broken characters** (a `?` or U+FFFD where an accent or apostrophe was lost: "blink?182") find nothing either.
5. **An empty name returns a real artist.** `search.php?s=` answered **Keane**. An empty or blank name must never be sent.
6. **Missing or malformed requests return an empty body with HTTP 200**, not JSON (`searchtrack.php?s=coldplay` with no title; `album.php?m=abc`). A not-found is `{"artists":null}` / `{"album":null}`. An empty body is therefore an error, never a "not found".
7. **A key TheAudioDB does not accept returns HTTP 404 `{"Message":"Not found"}`**, for every endpoint. A routing failure is 503 `{"Message":"Service temporarily unavailable"}`.
8. **Images** live on `r2.theaudiodb.com` (host spelled `r2.TheAudioDB.com` in 23 of the 480 image URLs sampled, and `www.theaudiodb.com` in 3). Appending `/medium` or `/small` gives a smaller file; `/large` and `/tiny` are 404. Original sizes: artist thumb 700x700, cut-out 700x700, logo 800x310, banner 1000x185, wide thumb 1000x562, fan art 1280x720, clear art 1000x562, album thumb 700x700, CD art 1000x1000, 3D case 1000x720, back cover about 470x400. `/medium` is not a fixed size (a thumb becomes 360, a CD 500).
9. **Text quirks.** Biographies carry citation markers (`...unfinished feature film.[17]`) and one ended in a cut-off `[`; paragraphs are separated by `\n`; no HTML. Empty values arrive as `""`, `null` or `"0"`; a `strTwitter` of `1` is junk; websites have no scheme (`www.sum41.com`).
10. **Names and ids.** The artist's MusicBrainz id is in `strMusicBrainzID`; an album's `strMusicBrainzID` is the release group and `strMusicBrainzArtistID` the artist. One alternate name per artist (`strArtistAlternate`: "Katy Hudson", "LĪVE", "Thirty Seconds To Mars").
11. **Years agree with the library.** Album year against the library's: 40 of 43 equal, 42 within one year; the exception (Bif Naked's self-titled 1994 against 2003) is a different album with the same name. Artist: 37 of 38 within one year of the formed or born year, all 38 within three. A solo artist's "formed" year is when the career began (Thundercat 2002, born 1984); this library's artist year is the birth year for a person and the formation year for a group.
12. **Rate limiting is documented but was not enforced** for the shared key: 165 sequential requests at 3.6 a second drew no 429. The design follows the documented limit (section 10) and handles a 429 correctly if it comes.

## 3. The standalone guarantee

**TheAudioDB must work for a user who has installed nothing else.** This is a requirement of the design, tested (section 11), not an assumption.

3.1 **No dependency of any kind on another plugin.** The assembly references only the host's `Chronicle.Plugins` interfaces, Serilog and `System.Text.Json`. It never looks up, loads, calls or names another plugin. The manifest declares no dependency. (Every existing plugin is already like this: none references another.)

3.2 **Everything the plugin needs it can get from the item and from TheAudioDB.** Name, parent name (the artist, for an album), year and `HierarchyLevel` arrive in the search context for every item. By name alone, measured: 38 of 40 sampled artists and 43 of 100 sampled albums matched with **no identifier at all** (the 2 artists not found were a "discography" folder name and one TheAudioDB lacks).

3.3 **Other plugins' ids are an accelerator, never a requirement.** If the item already carries a MusicBrainz id (put there by the MusicBrainz plugin, a file's tags, or anything else) the plugin uses it for an exact lookup; if it carries none, or a wrong kind, the plugin goes straight to the name route. The ids are read from the optional `KnownExternalIds` dictionary of the search context, treated as possibly null, possibly empty, possibly the wrong shape. Why the id route is still worth having (measured on 50 albums MusicBrainz had matched): by id TheAudioDB found 21, by name 21, **together 24**: the id finds what the name misses (Japanese names, typographic hyphens, a folder-noise artist like "The Corrs discography": all matched by id, none by name), the name finds what the id misses.

3.4 **Nothing it writes is required by anything else.** It publishes no cross-reference ids (section 5), so no other plugin's behaviour depends on it either.

3.5 **Degrades to nothing, never to wrong.** With no key accepted: the host reports the plugin as not authenticated and leaves items Pending. With TheAudioDB down: items stay Pending. With a name TheAudioDB does not have: not found. Nothing is ever guessed.

## 4. Types, levels and priority

* Declares `music`, `HierarchyLevels = 3`, labels `Artist, Album, Track`, verb `listened`, **identical to MusicBrainz, Last.fm and Deezer**, so the type is created the same way whichever plugin is installed first (this is what makes it standalone: it can create the type itself).
* **`EnrichOnly = true`.** One result per search is not a search: Add Media would show a single exact-name hit that looks like a result list. And the interactive search used by Add Media and the file scanner gives a provider **4 seconds** (`FileScanService`, against 25 for enrichment), which a plugin pacing itself at 2 seconds a request cannot meet. TheAudioDB enriches items that MusicBrainz, Deezer or the file scanner identified; it does not take part in identifying them. (This costs a standalone user nothing: file-scanned items are enriched by it exactly the same way.)
* `DefaultPriority = 40` (MusicBrainz 10, Last.fm 20, Deezer 30). Its artwork fills slots that are empty today; Metadata Assignment (section 12) decides what wins.
* **Declared fields**, which are what puts the plugin on the Metadata Assignment page for each field (level 0 uses `SupportedFields`; levels above use `LevelFields`, and a level the plugin lists nothing for is "no fields at this level"):
  * Level 0 (artist) `SupportedFields`: `title, overview, year, poster_url, backdrop_url, logo_url, banner_url, thumb_url, clearart_url, character_art_url, genres, tags, mood`.
  * `LevelFields[1]` (album): `title, overview, year, poster_url, disc_url, rating, genres, tags, label, mood`.
  * `LevelFields[2]` (track): `title, overview, runtime_minutes, poster_url, rating, genres, tags, bpm, mood, isrc`.
* The names must match the host's canonical field names exactly (`MetadataResolutionService.FieldMap`); a misspelled name makes the field invisible on the Assignment page.

## 5. Identity and ids

* External ids carry their kind: **`artist:111239`, `album:2109615`**. TheAudioDB's numbers are separate sequences but nothing guarantees they never overlap, and the host stores one id per source per item. Source name `theaudiodb`.
* **Fix match accepts:** a kind-prefixed id; a theaudiodb.com URL (`/artist/111239`, `/artist/111239-Coldplay`, `/album/2109615`, `/album/2109615-Coldplay-Parachutes`, all verified to resolve); `artist-mb:<uuid>` and `album-mb:<uuid>` (named for the endpoints they use). A bare number is refused with a message naming these forms, because it is ambiguous. `track:` and `/track/` URLs are refused with "tracks are not supported" rather than a silent failure.
* `GetAcceptedCrossRefPrefixes()` = `theaudiodb:` (nothing emits it today; harmless and correct).
* **Cross-reference ids published: none.** `ExtendedData.ids` is not written. TheAudioDB's `strMusicBrainzID` for an album is a release group and for a track a recording; publishing any of them under `ids` would make the host seed another plugin's enrichment row with an id of the wrong kind (this is the Open Library lesson: a `musicbrainz` key would have seeded MusicBrainz with a *book's* id). The MusicBrainz, Discogs, Spotify, Wikidata, AllMusic, ISNI and UPC ids go under `extendedData.external_ids`, where nothing acts on them and nothing is lost.
* **Reading ids, with the entity check.** Chronicle stores MusicBrainz ids as `artist:<uuid>`, `release-group:<uuid>`, `recording:<uuid>`; some items carry a different kind than their level (an album holding an `artist:` id). For an artist, accept `artist:<uuid>` or a bare uuid and **reject** any other prefix; for an album accept `release-group:<uuid>` or a bare uuid and reject any other prefix (the Fanart.tv plugin has exactly this check, for exactly this reason). The parent's artist id arrives as `parent_musicbrainz`; this plugin does not need it.
* **A returned record must agree with the id used.** After `artist-mb` the record's `strMusicBrainzID` must equal the requested id; after `album-mb` the record's must equal the requested release group. Anything else is "not found".
* Host rule to live with: an external id belongs to one Chronicle item library-wide. Where the library holds the same album twice, the second cannot take it; that shows in the Enrichment items list as "already belongs to a different item", as it does for Deezer and Open Library.
* The enrichment row remembers the matched id and reuses it on the next run instead of searching (host behaviour, fixed 2026-10-13). A Fix match to a new id therefore only takes effect through the host's normal reset; the plugin does nothing special.

## 6. Tracks

Decision (2026-10-14): tracks are in. The free tier makes a plain name search unreliable for them, so tracks are matched in three ways, in order, and **a record that is not provably the library's own is refused**:

1. **A known TheAudioDB id** (`track:<n>`): `track.php?h=`.
2. **A known MusicBrainz recording id** (`recording:<uuid>`, entity-checked): `track-mb.php?i=`, exact, independent of the album; the record's `strMusicBrainzID` must equal it.
3. **By name, only when the track's album is itself matched on TheAudioDB** (`parent_theaudiodb` holds `album:<id>`): `searchtrack.php?s=<the album's artist>&t=<title>`, accepted only if the record's `idAlbum` equals the parent's id (or its album name equals the library's), its artist equals the library's, and its title equals (edition qualifiers removed, recording qualifiers never). A track whose album is not matched makes **no request** for a name search.

Why: measured on 80 library tracks, a plain name search returned a track for 45, but the free tier returns one recording whichever album it is on, and for the 35 that carried a MusicBrainz recording id the returned recording was the right one in only 15 (the same song on another album: "War Machine" on *Black Ice* for *Back in Black*). A wrong recording would write a wrong duration, ISRC, tempo and key. By recording id only 20 of 49 were found. Requiring the album to agree turns the unreliable cases into refusals instead of wrong data.

Cost, honestly: 47,499 tracks, up to three requests each, **at the documented 30 a minute is on the order of 40 hours for a first pass**, in the background (the scheduler retries every 15 minutes while rows are pending). Tracks whose album TheAudioDB does not have cost at most the recording-id request. A premium key (100 a minute) cuts this by two thirds. Track lists (`track.php?m=<album>`) would make this cheaper on a premium key, but the free tier returns one track and the premium behaviour was not measurable, so they are not used.

Fields: title, overview (rare), runtime (minutes, exact milliseconds in extended data), poster (track thumb), genres, tags (style, mood, theme), rating (from 3 votes), `bpm`, `mood`, `isrc`; extended data holds key, open key, time signature, danceability, acousticness, valence, energy, liveness, instrumentalness, speechiness, plays and listeners, and the **music video** (YouTube link, director, company, view and like counts), with its stills as additional images. **Lyrics are not stored** (copyright; usually empty anyway).

## 7. Matching

One normaliser, the same as Open Library and Deezer: fold accents, lower-case, **fold typographic characters to ASCII first** (all hyphens and dashes to `-`, curly quotes to `'` and `"`, non-breaking space to space), treat `&` and "and" as equal, drop a leading article, drop everything that is not a letter or digit. Names lose a trailing "(YYYY)"; artist names lose a trailing folder word ("discography" and the editable list in section 9). A `?` or U+FFFD in a library name is left **out** of the query, not replaced (every word must match).

**An empty or blank name makes no request** (finding 5).

### 7.1 Artist (level 0)

1. Known `theaudiodb` id (`artist:<n>`) -> `artist.php?i=` (score 100, `IdentifierMatch`).
2. Known MusicBrainz artist id (entity-checked, 5) -> `artist-mb.php?i=` (100, `IdentifierMatch`). A miss falls through to 3.
3. By name, at most two searches: the cleaned name; then, if it differs, the name with a leading "The" added or removed. Accept only a record whose `strArtist` or `strArtistAlternate` equals the item's name after normalising.
4. Score: equal `strArtist` 85; equal alternate name 75; library year within 1 of the born or formed year +10; library year known and more than 3 away from both **caps the score at 45, below the host's gate (50)**.
5. Result: at most one candidate. No equal name -> no result. (Never match a similar name; the service already does the forgiving part.)

Budget: 1 request with an id, at most 2 without; the host's fetch by id afterwards is answered from the cache.

### 7.2 Album (level 1)

1. Known `theaudiodb` id -> `album.php?m=`.
2. Known MusicBrainz release group (entity-checked) -> `album-mb.php?i=` (100, `IdentifierMatch`). A miss falls through.
3. By name, at most two searches (so at most three requests, four with the artist lookup below):
   * **Artist argument:** TheAudioDB's own spelling when the parent artist already carries a `theaudiodb` id (`parent_theaudiodb`): one cached `artist.php?i=` (shared by every album of that artist, so it costs about one request per artist). Otherwise the parent's name, normalised and cleaned as in 7.1.
   * **Title variants, in order:** the title with typography folded to ASCII; then, if it differs, the title **without an edition qualifier**; then, if budget remains, the other form of a leading "The".
   * **Edition qualifiers are only these:** "(Deluxe Edition)", "(Remastered YYYY)", "(Expanded Edition)", "(Bonus Track Version)", "(Anniversary Edition)", "(Super Deluxe)", "- Deluxe", and similar; the list is editable (section 9). **A qualifier that changes the recording is never removed**: remix, live, demo, acoustic, instrumental, radio edit, karaoke, mix, single version. The prototype showed why: stripping "(Remixes)" and "(Tough Love remix)" turned two remix releases into matches of the original single, and 2 of the 5 titles that variants "recovered" were these wrong matches. 312 albums in this library carry a recording-changing qualifier; 13 carry an edition one.
4. Verify the one record returned:
   * Title equal after normalising: 60; equal after removing an edition qualifier from either side: 55.
   * Artist equal (name or alternate name): +30. **A known artist that does not match caps the score at 35.**
   * Year: library year within 1 +8. Library year known and more than 2 away **caps the score at 40, unless the library title itself carries an edition qualifier** (a remaster's year differs by design). This rejects a different album with the same name (measured: Bif Naked 1994 against 2003; "Greatest Hits" 1981 against 2021).
   * The host gate is 50: a name-only match with artist equal is 90 and passes, a year disagreement fails.
5. Cache the record by id as it is returned, so the host's `GetByIdAsync` costs nothing.

Budget per call: at most 4 requests, which at the default pace is 8 seconds plus latency, inside the 25 seconds Chronicle gives an enrichment call (the 4-second interactive search never reaches an `EnrichOnly` plugin).

### 7.3 What the library holds is not used to choose

Deezer and Open Library confirmed a candidate by the library's children. TheAudioDB cannot: the free tier returns one track, not a list. The year check above is the substitute; Fix match is the remedy.

## 8. Field mapping

Slots are the host's eight canonical image fields plus additional images.

| Chronicle field | Artist | Album |
|---|---|---|
| Title | `strArtist` | `strAlbum` |
| AlternateNames | `strArtistAlternate`, the library's own wording of the name (so the host's name gate accepts "The Corrs discography") | the library's own wording of the title |
| Overview | `strBiography` (language, below) | `strDescription` (language) |
| Year | born year for a person (`intMembers` is 1 or a gender is given), else formed year; the other in extendedData | `intYearReleased` |
| `poster_url` | `strArtistThumb` | `strAlbumThumbHQ` when present, else `strAlbumThumb` |
| `backdrop_url` | `strArtistFanart` | none |
| `logo_url` | `strArtistLogo` | none |
| `banner_url` | `strArtistBanner` | none |
| `thumb_url` | `strArtistWideThumb` | none |
| `clearart_url` | `strArtistClearart` | none |
| `character_art_url` | `strArtistCutout` (a transparent cut-out of the artist, which is what the slot is for) | none |
| `disc_url` | none | `strAlbumCDart` |
| Additional images (typed) | `Fanart` (fan art 2 to 4) | `Back`, `Spine`, `3D case`, `3D flat`, `3D face`, `3D thumb` |
| Genres | `strGenre` | `strGenre` |
| Tags | `strStyle`, `strMood` | `strStyle`, `strMood`, `strTheme` |
| Rating | none | `intScore` (0 to 10, the scale MusicBrainz already stores here), **only when `intScoreVotes` is at least the setting (default 3)**; the sample's scores were from 1 to 8 votes |
| Label | `strLabel` in extendedData | `strLabel` (promoted: `label` is a declared field) |
| Mood | `strMood` | `strMood` (promoted: `mood`) |

* **Not invented:** no cast on an album (it would be a second people record for the same artist next to Deezer's: the host keys people on the id string, and a different id for the same artist creates a duplicate; revisit when people dedupe for music is settled); no rating on an artist; no poster for a missing image.
* **Cleaning:** citation markers `[n]` and a trailing partial `[` removed from every text; `\r\n` to `\n`; non-breaking and doubled spaces collapsed; paragraphs kept. `http://` image URLs become `https://`. Empty, `"0"` and `null` are absent. Websites and socials get `https://` when they have no scheme; a `strTwitter` that is not a plausible handle (the sample had `1`) is dropped.
* **ExtendedData** keeps everything else, nothing dropped: ids under `external_ids` (never `ids`, section 5), artist: `born_year`, `formed_year`, `died_year`, `disbanded`, `country`, `country_code`, `gender`, `members`, `style`, `genre`, `mood`, `label`, `isni`, `website`, `facebook`, `twitter` (when valid), `popularity`, `followers`, `charted`, `lastfm_chart`, all image URLs by kind, the theaudiodb.com link; album: `style`, `mood`, `theme`, `speed`, `label`, `release_format` (Album, Single, EP, Live, Video album), `sales`, `score`, `score_votes`, `loved`, `popularity`, `review` (the critic's text, kept in full), `location`, all image URLs by kind, the link.
* **Not stored:** lyrics, anything keyed to the user's session.
* **Language.** The default is English (`strBiography`, `strDescription` carry no suffix); another language uses `strBiography<CODE>` and falls back to English when empty. Codes in the service's data: CN, DE, ES, FR, HU, IL, IT, JP, NL, NO, PL, PT, RU, SE.
* **The host checks every image URL** (one request each, in parallel) before storing it, so the number of images per item is deliberate: at most one of each kind plus the fan art and album extras above, about 12.

## 9. Settings

Per the "no hardcoding" rule, everything a user may want to tune is a setting, not code.

| Key | Type | Default | Meaning |
|-----|------|---------|---------|
| `api_key` | Password | `123` | The free key. Paste a premium key to lift the limits. Encrypted by the host; never logged (section 10). Documented as public for `123`, secret for a premium key. |
| `requests_per_minute` | Number | `30` | The documented free limit. 100 for a premium key. 1 to 120. |
| `language` | Dropdown | English | Language of biographies and descriptions (section 8). |
| `image_size` | Dropdown | Original | Original, medium (`/medium`), small (`/small`). |
| `minimum_rating_votes` | Number | `3` | An album score from fewer votes is not stored as a rating (it stays in extendedData). |
| `artist_folder_words` | TextArea | `discography` | One per line. A trailing word stripped from artist names before searching (this library has 7 artists named "... discography"). |
| `edition_words` | TextArea | the list in 7.2 | One per line. Words that mark an edition rather than a different recording. Never put remix, live or demo here (a note in the setting says why). |

User-Agent `Chronicle-TheAudioDB/<version>`. No other account or key.

## 10. Rate limiting, errors, caching

* **One limiter per plugin instance, shared by every caller** (enrichment, search during a file scan, the health check): at least `60 / requests_per_minute` seconds between requests (2 s at the default). A caller waits in the limiter only as long as its own cancellation allows; Chronicle's 25-second ceiling per enrichment call means a call is never allowed to queue past it. Enrichment concurrency stays at the default of 1 (the plugin has its own limiter, so more concurrency would only queue).
* **HTTP 429 is thrown as `HttpRequestException` with status 429**, never waited out: the host leaves the item Pending, stops the pass, and the scheduler retries the task 15 minutes later (the documented penalty is one minute). This is the AniList lesson: waiting out a long limit past the 25-second ceiling turned every search into "not found". A `Retry-After` of 8 seconds or less is waited once.
* **HTTP 404 with `{"Message":"Not found"}` is a rejected key**: a `PluginAuthException` whose message names the fix ("TheAudioDB did not accept the API key. Free use needs the key 123; a premium key is on your TheAudioDB profile."), so the host shows it and stops. (Every endpoint answers this for a bad key, so a 404 on a well-formed request is never a "not found".)
* **503** and other 5xx, timeouts and network errors propagate; Chronicle retries transient ones twice itself.
* **A 200 with an empty body, or a body that is not JSON, is an error** (finding 6), not "not found": it propagates. Only `{"artists":null}` and `{"album":null}` mean not found. A body that parses but lacks the expected key is also an error.
* **The key never appears in a log line, an exception message or a result.** It travels in the URL path, so every log and every error that quotes a URL passes through one redaction function (tested, as OMDb's was).
* **Cache:** ten minutes, in memory, of artist records, album records (including the ones a search returned) and canonical artist names, so the host's fetch by id after a search costs no request and an artist's albums share one artist lookup. Bounded size.
* Images are never downloaded in bulk: URLs only. `GetImageAsync` fetches one image on request, only from `theaudiodb.com` hosts (any case), with a size cap.
* **Health check:** one `artist.php?i=111239`; healthy when it returns a record; a rejected key reports as such.

**Budget for the first pass over this library:** 247 artists at 1 request; 3,859 albums with a release-group id at about 2.1 (the lookup, then up to two name searches for the 52% TheAudioDB lacks); 3,750 without an id at about 1.7; plus about one artist lookup per artist: **about 16,000 requests**. At the documented 30 a minute that is **about 9 hours**, in the background, drained by the daily task and its 15-minute retries; at the 3.6 a second measured on the shared key it would be about 75 minutes, and a premium key (100 a minute) about 2.7 hours. Re-sync is off by default.

## 11. Tests (all required before release)

**Standalone tests: the release blocker.** They run with no other plugin present and, where relevant, with every other source of ids absent:

* `Standalone_Artist_IsFoundByNameAlone`: a context with `KnownExternalIds = null`, then with an empty dictionary, then with only unrelated keys (`tmdb`, `lastfm`): the artist is matched by name, with the same score each time.
* `Standalone_Album_IsFoundByArtistAndTitleAlone`: same three contexts, no `parent_*` keys.
* `Standalone_WrongKindOfMusicBrainzId_IsIgnored_NotTrusted`: an artist item holding `release-group:<uuid>`, an album item holding `artist:<uuid>`, a malformed uuid, an empty string: no id lookup is made, the name route runs, the result is correct.
* `Standalone_AssemblyReferencesNoOtherPlugin`: the plugin assembly's referenced assemblies contain only the framework, `Chronicle.Plugins`, Serilog and `System.Text.Json`; none named `Chronicle.Plugin.*` or `Chronicle.Core`/`Chronicle.Services`.
* `Standalone_ManifestDeclaresNoDependency`: `manifest.json` has no dependency or "requires" entry, and the plugin's code contains no other plugin's id (a scan of the assembly's strings for `chronicle.plugin.` other than its own).
* `Standalone_MediaTypeCreation_MatchesTheOtherMusicPlugins`: the declared `music` type is byte-for-byte the one MusicBrainz, Last.fm and Deezer declare (levels, labels, verb), so it can create the type when it is the only music plugin.
* `Standalone_NoCrossReferenceIdsAreEverPublished`: `ExtendedData` never contains an `ids` key, for any fixture.
* `Standalone_WithNothingInstalledElse_FullEnrichmentRunSucceeds`: a fake-HTTP end-to-end run of an artist and two of its albums, no id anywhere, each ending with its data.

**Unit tests**, on the recorded real responses in `tests/fixtures/` (artist, album, null results, the 404 and 503 bodies, the quirk records): the normaliser (every typographic character in finding 3, `&`/"and", leading articles, "(YYYY)", broken characters left out); folder words; edition qualifiers against recording-changing ones (**every prototype case: "(Remixes)", "(Tough Love remix)", "(Live ...)", "(Demo)", "(Radio Edit)" are never stripped, "(Deluxe Edition)" and "(Remastered 2011)" are**); artist verification (equal, alternate, year caps, namesake); album verification (equal, artist mismatch cap, year cap, the remaster exception, the self-titled album, "Greatest Hits"); id and URL parsing (every form in section 5, bare numbers and `track` refused with their messages); the entity check on MusicBrainz ids; the returned-record-agrees-with-the-id check; born against formed year; text cleaning (`[17]`, a trailing `[`, whitespace, the junk `strTwitter`, schemeless websites); image URL building (sizes, `http` to `https`, host case, a wrong-case host accepted); field mapping for every slot in section 8; the vote threshold; language and its English fallback; the declared fields against the host's `FieldMap` names (a test that every declared field exists there, so a typo cannot hide the plugin).

**Contract tests**, fake HTTP handler: request budget (never more than 4 per call, 1 with an id, 0 for an empty name and for any track); **an empty or blank name makes no request**; a 429 is thrown and not waited out; a `Retry-After` of 8 seconds or less is waited once; a 404 `Not found` is `PluginAuthException`; a 503 propagates; an empty body and a non-JSON body are errors; `{"artists":null}` is empty; pacing at 30 and at 100 a minute; the limiter is shared by concurrent callers; the cache stops repeat fetches and a search result answers the host's fetch by id; one artist lookup serves a second album; **the key never appears in a log, a message or an exception** (a logging sink that records everything, checked for the key); `GetImageAsync` refuses a non-theaudiodb host; a track item makes no request.

**Host test (Chronicle repo):** a plugin declaring `LevelFields[2] = []` has its track rows removed and not re-seeded (the existing SIMKL test covers the mechanism; add the music case), and clearing a TheAudioDB match then re-running searches again (the regression for the stale enrichment id, shared with Deezer).

**Live tests**, off unless `THEAUDIODB_LIVE=1` (and `THEAUDIODB_KEY` for a premium key): a fixed list of real artists and albums, including the ones that taught each finding in section 2; pacing respected.

**Manifest test:** id, version, tasks, supported types and the declared level fields match the code.

## 12. Making the data show: Metadata Assignment (found while designing this)

**A plugin's data is stored the moment it enriches, but it reaches an item's displayed fields only if the plugin is in that field's Metadata Assignment list.** For a field that has a saved list, `MetadataResolutionService` consults only the plugins on it; for a field with no saved list it takes the first plugin that has a value. A newly installed plugin is not added to saved lists.

Checked on this library's saved music configuration (`metadata_assignment.config`, 2026-10-14): **Last.fm and Deezer are on no music list at all.** Consequences already visible: every one of the 247 artists shows MusicBrainz's one-line overview ("Group · Canada") although Last.fm holds a long biography for 240 of them and Wikipedia for 219 (MusicBrainz is first on the list, and any non-empty value wins); Deezer's 1900-pixel covers are not used. That is the existing configuration doing what it was told, not a defect in those plugins; it is worth knowing before TheAudioDB is judged by what it shows.

For TheAudioDB the effect is larger, because most of what it brings is in fields that are **empty today**:

| Field (list) | Today | After adding TheAudioDB (at the end of the list) |
|---|---|---|
| `music` thumb_url, clearart_url, character_art_url | no plugin supplies any; empty for all 247 | filled from TheAudioDB wherever it has them |
| `music` logo_url, banner_url, backdrop_url | `fanarttv` only | TheAudioDB fills the 41, 90, 38 artists Fanart.tv left empty |
| `music.1` disc_url | `fanarttv`; empty for all 7,599 | CD art for about 2,500 |
| `music.1` rating, genres | `musicbrainz` only | scores (3+ votes) and genres where MusicBrainz has none (6,486 and 5,873 empty) |

Rollout steps (done once, by the administrator, after the first successful enrichment):

1. Settings -> Metadata Assignment -> Music (artist, album): add TheAudioDB **at the end** of each list above, so it fills gaps and does not displace a source the user has chosen. Moving it up for the artwork fields is the user's call.
2. For the text fields (overview, genres, tags) the user decides; adding it last changes nothing for items that already have a value.

**Decision 6 below asks whether to change this for every plugin, not just this one** (the host could append a newly installed plugin to every list it declares fields for, at its default priority). That is a host change with consequences for every type; it is not part of this plugin.

## 13. Evidence from the prototype

Samples drawn at random from this library, queried live on 2026-10-14. Library names and ids are not stored in the repo; the API responses are, in `tests/fixtures/`.

* **Artists, 40:** found 38. The two missing: "The Corrs discography" (found by its MusicBrainz id, and by name once the folder word is stripped) and a very obscure artist TheAudioDB lacks. **All 38 matched the library's own MusicBrainz id**, so there were no wrong matches. "Thirty Seconds to Mars" was found through TheAudioDB's alternate name.
* **Albums, 100 (50 MusicBrainz had matched, 50 it had not):** by name 21 and 22 found, none wrong (title and year agreed in all 43 except the self-titled Bif Naked album, which the year rule rejects). By MusicBrainz id, on the 50 that had one: 21 found; with the name route as well, 24. The missing albums are overwhelmingly singles, remixes, live releases, "discography" folders and artists' demo EPs TheAudioDB does not carry; 312 of the library's albums are such releases.
* **Re-trying the 57 misses with title variants:** 5 recovered, 3 correctly (typographic apostrophe or hyphen), 2 **wrongly** (remix releases matched to the original single): the reason the recording-changing qualifiers are never stripped.
* **Tracks, 80:** section 6.
* **Fill rates**, on the 38 artists and 43 albums found: section 1.
* **Limits:** 165 sequential requests, 3.6 a second, no throttling (finding 12). One key (`123`) for all free users; the retired `2` answers 404.
* **Not measured: premium behaviour** (result limits, rate, any difference in fields). The design reads responses by field name and never depends on a count, so a premium key's longer lists are ignored harmlessly; the live tests accept a premium key from the environment so this can be checked later.

## 14. Background tasks

`fetch-missing-metadata` (daily 05:35, enabled) and `resync-all-metadata` (weekly, Wednesday 03:35, disabled), the host's built-in handlers, times offset from MusicBrainz (every 15 minutes), Last.fm, Deezer (04:50), Fanart.tv and Wikipedia (05:00), Open Library (05:30). The scheduler retries `fetch-missing-metadata` 15 minutes after a run that finishes with rows still Pending, so a 429 or a long first pass continues by itself. After the Background Tasks page fix of 2026-10-14, Run Now on the Enrichment Status row runs that task and shows its state.

## 15. Release and docs

README in the standard plugin format (badge, Plugin ID / Version / Auth block, supported types with fields, id forms, how matching works, settings, tasks, structure, building), replacing the scaffold's; `manifest.json` with `entry_type`, `iconUrl` (`https://www.theaudiodb.com/favicon.ico`; checked at build time), brand colours, `fixMatchHint` (the forms in section 5), `settings: []` (the schema comes from the plugin), tasks above; `plugins.json` entry (tags `music`, `metadata`); a GitHub release with one zip (DLL, deps.json, manifest).

Places that list plugins by hand, all to be updated (Deezer needed each):

* `scripts/Deploy-Plugin.ps1` and `scripts/RunTestEnvironment.ps1`: a plugin missing from them is refused or not built.
* `src/Chronicle.Services/Plugins/PluginCatalogSeeds.cs`: one line, `new("chronicle.plugin.theaudiodb", "thegoddamnbeckster/Chronicle.Plugin.TheAudioDB", ["music", "metadata"])`. **This is the only host code change.**
* Docs: replace `docs/plugins/PLUGIN_THEAUDIODB.md` (it names the dead key `2`, `searchalbum`-less endpoints and a `strBio{Lang}` pattern that is wrong, and an `include_music_videos` setting that does not exist: music videos are a link on each track), update `docs/PLUGIN_CATALOGUE.md` (status, key), `docs/plugins/PLUGIN_MUSIC_DATABASES_INDEX.md`.

First deploy: the plugin's file hash must be approved on the Plugins page (Background Tasks now says so when it is waiting); the key setting then needs saving once (`123`, or a premium key).

## 16. Decisions (taken 2026-10-14)

1. **Tracks:** included (section 6).
2. **Pace:** the documented 30 a minute, obeyed (setting `requests_per_minute`).
3. **`EnrichOnly`:** yes.
4. **Priority 40**, **cut-out as `character_art_url`**, **rating only from 3 votes (setting)**: as recommended.
5. **Metadata Assignment (host):** a plugin that is on no list is now used as a last resort when nothing on the list has a value, and the Assignment page appends it to each list it supports (section 12). It still has to be placed by an administrator to *win* a field.

## 17. Risks

* **Terms of use.** Free use is for the key `123`, with a 30-a-minute limit and a premium tier for more; this is a personal self-hosted library, which fits. The service says it was abused when unlimited and restricted methods since; the design already works within the tightest tier. If TheAudioDB changes the free tier again, items stay Pending or end "not found" and nothing breaks.
* **One shared free key.** All free users share `123`; the service is the one rate-limiting it. If the key is throttled by others' traffic, a 429 is handled as above.
* **Coverage ceiling.** About 46% of this library's albums, because its albums are many singles, remixes and live recordings. "Not found" is the honest answer for them.
* **A different album with the same name** is mitigated by the year rule, not eliminated (free tier: one result). Fix match corrects it.
* **Namesake artists.** A free search returns one artist; two bands with one name get whichever TheAudioDB chooses. The id route (the library has MusicBrainz ids for all 247 artists) removes this for the existing library; the year check catches most of the rest.
* **Stale data.** Community-edited; `strLocked` is exposed in extendedData.
* **The first pass is long** (about 9 hours for artists and albums, then tracks at up to three requests each), by design and in the background.
* **Premium untested.** See section 13.

## 18. What building it changed

* **Tracks included** (section 6), at the owner's direction, with the album-agreement rule above instead of a bare name search.
* **Request budget** is counted per call, not from the client's running total, so concurrent callers cannot starve each other of their four requests.
* **A toggled name** ("The" added to a name that is only a broken character) is never searched: it would have asked for the artist "The".
* **The host** needed three changes the design had not foreseen, found while checking why other plugins' data did not show: images whose type the frontend did not know were dropped from Additional Images (Open Library's "cover"); plugins on no assignment list were never used; and a book and its audiobook sharing a provider id were treated as duplicates. All three are fixed in Chronicle (section 12 and docs/PLUGIN_AUTHORING.md, "Making your data show").
* **Not stored:** lyrics, and the other languages' texts (change the language setting and re-sync instead).
