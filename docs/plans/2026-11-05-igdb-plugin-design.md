# IGDB plugin: design

Checklist: see docs/PLUGIN_NEW_TYPE_CHECKLIST.md (added after v1.0.0 shipped without developers on the game page and with IGDB last on the Metadata Assignment page; fixed in v1.0.1).

Status: **built and released (2026-11-05): plugin v1.0.0 on GitHub (thegoddamnbeckster/Chronicle.Plugin.IGDB), 300 tests passing, running in the dev environment; the live tests against IGDB are still to be run with the owner's credentials.** Section 21 records what building it changed, including that the type is named `game`, not `games` as this document first said; where an earlier section says `games`, section 21 governs. Section 17 holds what the live probe measured; the sections it changed say so. Repo: `Chronicle.Plugin.IGDB` (public, a stale scaffold: every method throws `NotImplementedException`, it is written against the old `SearchAsync(string)` interface, and its manifest names the type `game`; it is replaced, not extended). Plugin id `chronicle.plugin.igdb`, source name `igdb`.

This document was written, then reviewed adversarially against the host source and the IGDB docs. The review found three blockers and a number of wrong or missing statements; all are folded in, and section 20 records what changed so the next reader can see why.

**Owner rule, restated 2026-11-05: a metadata plugin grabs everything its source has for a title. No caps, no trimming, no filtering of data out.** An earlier draft of this design added limits on screenshots, artworks, keywords, alternative names, similar games and external ids, an exclusion list of game types, and a vote threshold on ratings; all of that was removed (section 20). Limits on *requests* (pacing, timeouts, back-off) remain; limits on *data kept* do not. Where a record is junk for one purpose (an executable name inside alternative names, which would pollute name matching), it is **moved to its own field**, not dropped; and the complete source record is kept verbatim (7.2) so nothing the mapper does not know about is lost.

**Each plugin also stands alone** (3): IGDB and Wikipedia must each work with the other absent, and the order they are built or released in does not matter.

Every claim about IGDB is one of two kinds:

* **Docs-verified** (api-docs.igdb.com, read 2026-11-05, and re-read by the reviewer): stated as fact.
* **Measured** (the live probe of 2026-11-05, section 17.1): stated as fact with the numbers. Items still unmeasured are marked *unverified* and listed in 2.3.

Every claim about the **host** was checked against its source, with the file named, because the central lesson of the earlier plugins is that *what the plugin throws decides what happens to the item*, and the host treats the same exception differently on each of its callers (section 9.1).

This plugin creates the first **game** library in Chronicle. It has no other plugin to lean on (no Steam, RAWG or scanner is built), so the standalone guarantee (3) and the new-type checklist (12) carry more weight than they did for TheAudioDB.

## 1. Purpose and scope

The only metadata source for the `game` media type (first drafted here as `games`; see 21), and the template for the games plugins that follow (RAWG, Steam, LaunchBox, IsThereAnyDeal are scaffolds today).

In scope: one flat item per game; title, summary, year, cover, artwork, screenshots, genres, themes, modes, perspectives, platforms, companies, ratings, age ratings, release dates, franchise/series, related games (DLC, expansions, remakes, ports), websites, cross-reference ids, videos (as links), and optionally time to beat.

Out of scope, on purpose:

* Importing a person's play history or library (IGDB has no accounts; Steam/GOG plugins do that).
* Scanning game files (no scan hints exist for games; 12.7).
* Characters, companies and platforms as Chronicle items. Companies are **not** put in `cast`/`crew`, because those create People records.
* Grouping a franchise into a Chronicle collection (the collection machinery is movie-only; 12.5, decision 3).
* Webhooks, PopScore, data dumps, protobuf. Nothing here needs them.
* Anything that scrapes igdb.com. Only the API.

## 2. What IGDB gives us

### 2.1 The API (docs-verified)

* Base `https://api.igdb.com/v4/{endpoint}`. **POST** with an Apicalypse text body (`fields name,rating; where id = 1942; limit 10;`). Headers `Client-ID: <id>` and `Authorization: Bearer <token>`. `Accept: application/json`.
* **Auth:** a Twitch account **with two-factor authentication on**, an application registered in the Twitch developer portal (client type *Confidential*, OAuth redirect `localhost`), then a *client credentials* token: `POST https://id.twitch.tv/oauth2/token` with `client_id`, `client_secret`, `grant_type=client_credentials` (the docs show them in the query string). Response `{"access_token","expires_in","token_type":"bearer"}`; the documented example is 5,587,808 seconds. No scopes. The IGDB FAQ says a token is active for 60 days and **an application can have only 25 active tokens**.
* **Rate limit: 4 requests per second; HTTP 429 above it. Up to 8 requests open at once.**
* **Free for non-commercial use** under the Twitch Developer Service Agreement. The Getting Started page says commercial use needs a partnership (partner@igdb.com); the FAQ words it as free for both with a partnership and attribution expected for commercial. The two pages differ, so the README says "non-commercial, personal use" and nothing more. The FAQ allows and prefers local caching.
* `limit` default **10**, maximum **500**; `offset` pages.
* **Search:** `search "text";` on `/games`, with results documented as ordered by similarity. It can be combined with `where` (the docs' own example excludes editions with `where version_parent = null`). The separate `/search` endpoint mixes characters, collections, platforms, themes and games and is not used.
* Filters: `=`, `!=`, `>`, `<`, `~` (case-insensitive) and `"text"*` wildcards, `null`, `(a,b)` any-of, `[a,b]` all-of, `&`, `|`.
* **Expanders:** a dot reaches into a reference (`genres.name`, `cover.image_id`, `involved_companies.company.name`), so one request can return a game with everything under it. `fields *` returns every field.
* **Multi-query:** `POST /multiquery`, **at most 10 queries**; the answer is an array of `{name, result}`.
* **Images:** the API returns an `image_id` and a protocol-relative `url` (`//images.igdb.com/igdb/image/upload/t_thumb/<id>.jpg`, always the thumb size). The address is `https://images.igdb.com/igdb/image/upload/t_{size}/{image_id}.jpg`. Sizes: `cover_small` 90x128, `cover_big` 264x374, `screenshot_med` 569x320, `screenshot_big` 889x500, `screenshot_huge` 1280x720, `logo_med` 284x160, `thumb` 90x90, `micro` 35x35, `720p` 1280x720, `1080p` 1920x1080; any takes `_2x` (so `cover_big_2x` is 528x748). **Images removed from igdb.com remain for 30 days**, then go; images are therefore re-read on every re-sync.
* **Deprecations are live.** The Game endpoint still lists, as deprecated, `category` (use `game_type`), `status` (use `game_status`), `collection` (use `collections`) and `follows`; artwork (`artwork_type` -> `image_type`), external game (`category` -> `external_game_source`, `media` -> `game_release_format`), release date (`category`, `region`), website (`category` -> `type`) and age rating (`category`, `rating`, `content_descriptions`) all carry deprecated fields. A plugin built on one breaks on an unannounced day (9.7). The game's own `rating` is current ("Average IGDB user rating"); the deprecated enums are on `age_ratings`.

Game fields (names): `name, slug, url, summary, storyline, first_release_date` (Unix time), `game_type, game_status, version_parent, version_title, parent_game, cover, artworks, screenshots, videos, genres, themes, game_modes, player_perspectives, keywords, platforms, involved_companies, franchise, franchises, collections, alternative_names, game_localizations, language_supports, release_dates, age_ratings, websites, external_games, game_engines, multiplayer_modes, executables, rating, rating_count, aggregated_rating, aggregated_rating_count, total_rating, total_rating_count, hypes, similar_games, dlcs, expansions, standalone_expansions, expanded_games, remakes, remasters, ports, forks, bundles, tags, created_at, updated_at, checksum`.

Related endpoints: `game_time_to_beats` (`hastily, normally, completely` in **seconds**, `count`, `game_id`); `involved_companies` (`company, developer, publisher, porting, supporting`); `external_games` (`external_game_source, uid, url, name, year, platform`); `websites` (`url, type, trusted`) with `website_types` (`type` string); `release_dates` (`date, human, y, m, d, date_format, platform, release_region, status`); `game_videos` (`name, video_id`, a YouTube id); `covers`, `screenshots`, `artworks` (`image_id, width, height, animated, alpha_channel`); lookup tables `game_types` (`type` string), `game_statuses`, `external_game_sources` (`name`).

The old numeric `category` enum (main_game 0, dlc_addon 1, expansion 2, bundle 3, standalone_expansion 4, mod 5, episode 6, season 7, remake 8, remaster 9, expanded_game 10, port 11, fork 12, pack 13, update 14) is for orientation only. **The plugin never uses these numbers** (6.2).

### 2.2 What the design takes from that

1. One search request can return complete game records, every field and expansion (measured: 10 results, 80 KB, 0.24 s), so nothing is left for a swallowed follow-up to lose; only the time to beat and the characters live on other endpoints (7.1).
2. At 4 requests a second a first pass over a large library is minutes to hours, not days (9.9). IGDB is fast enough for the interactive search, unlike TheAudioDB, so it stays out of `EnrichOnly`; this matters because it is the only games plugin.
3. IGDB's `search` is fuzzy and returns clones (editions, remasters, ports, mods, bundles, DLC) next to the game. **The plugin verifies; it does not trust the ranking.**
4. The token is long-lived but scarce (25 active), so it is minted once and shared (10).

### 2.3 What is still unmeasured

After three live probes (section 17) the remaining unknowns are small: the sustained, steady-state request rate IGDB tolerates over a long run (a burst of 150 requests at about 80 a second drew 32 429s; a few minutes at 3 a second drew none); whether a multiquery counts as one request toward the limit (it returned 200 in every test, but the limit was only reached by plain requests); and the behaviour of the Twitch token endpoint during a long outage. Everything else previously marked unverified is now measured.

## 3. The standalone guarantee

IGDB **must work for a user who has installed nothing else**, and it is also the plugin that has to *create the media type*.

3.1 No dependency on any other plugin: the assembly references only `Chronicle.Plugins`, Serilog and `System.Text.Json`; the manifest declares no dependency; no other plugin's id appears in its code.

3.2 Everything it needs comes from the item and from IGDB. The enrichment pass supplies the name, the year and a cleaned alternative title; **Add Media supplies only the typed query and the type** (9.1), so the interactive path must work from a bare name.

3.3 Other plugins' ids are an accelerator, never a requirement. `KnownExternalIds` may be null, empty, or hold keys of the wrong shape. `igdb` is read as a positive integer; `steam` as digits only; anything else is ignored and the name route runs.

3.4 It publishes **no** cross-reference ids under `ExtendedData.ids` (5.3).

3.5 It degrades to nothing, never to wrong, and **never silently to a permanent state**: no credentials -> `AuthFailed`, which the host shows as needing attention and leaves fixable (9.2); IGDB down -> items stay Pending; a title IGDB does not have -> NotFound, which the host treats as **terminal until a person resets it** (9.1), so NotFound is returned **only** for a genuine, well-formed empty answer and never for a failure.

3.6 It declares the `game` media type completely, so with this as the only plugin the type is created correctly (4), and declares it **exactly as the IMDb plugin already does** (21). The type is created when Chronicle **starts**, not when the plugin is installed (12.1).

## 4. Types, levels and priority

One media type, declared by this plugin and (later) identically by every other games plugin:

| Property | Value | Why |
|---|---|---|
| `MediaTypeName` | `game` (see 21; first drafted as `games`) | The name the shipped IMDb and Wikipedia plugins, the eight game scaffolds and the existing database row already use |
| `DisplayName` | Games | |
| `HierarchyLevels` / `HierarchyLabels` | 1 / `["Game"]` | A game is one item. DLC, expansions, remakes and ports are separate IGDB games kept as **links**, not child items |
| `InteractionVerb` | `played` | The host builds "Plan to Play / Playing / Replaying" from it (`typeWording.ts`) |
| `ProgressUnit` | `hours` | A free-text label nothing consumes (checked) |
| `IsTrackable` | true | |
| `ProviderFamily` | none | `movie`/`tv`/`music` are the vocabulary Kodi and the other providers speak; **only plugins that name `game` exactly serve it** |
| `ScanStrategy` | none | No scanner understands games (12.7) |
| `CastHeading` / `CastAreActors` | `Developers` / false | For a later plugin that does supply people |
| `Aliases` | `games`, `videogame`, `video_game` | **Client wording only** (a client saying "game" resolves to the type). Aliases do **not** make a plugin that declares `game` serve `games`; provider matching ignores them (12.12) |
| `ImportShape` | none | Flat |
| `EnrichOnly` | **false** | Fast enough and the only identity source for the type |
| `DefaultPriority` | **10** | RAWG would be 20, Steam 30 |

**Declared fields** (they put the plugin on the Metadata Assignment page; names must be the host's canonical ones in `MetadataResolutionService.FieldMap`, proven by a test): `title, overview, year, poster_url, backdrop_url, rating, genres, tags`, plus `runtime_minutes` only when `use_time_to_beat_as_runtime` is on. A change to that setting takes effect on the host's plugin reload, which re-reads the declaration.

Not declared, deliberately: `cast`, `crew` (companies are not people), `collection` (12.5), `label`/`language` (`label` means a record label elsewhere), `thumb_url`, `logo_url`, `banner_url`, `clearart_url`, `disc_url`, `character_art_url` (IGDB has no such art for games). **Gap for the owner (decision 4):** developer, publisher, platforms and release date are what people expect on a game page and the host has no canonical field for them. They are stored losslessly in extended data and shown in IGDB's own box, but cannot be ordered between plugins on Metadata Assignment until the host gains those field names.

## 5. Identity and ids

### 5.1 The external id

The ExternalId is the **bare IGDB game id** (`1942`). There is one kind of record, so unlike TheAudioDB there is no kind to prefix. Source name `igdb`.

### 5.2 Fix match accepts

The host passes the input **raw** (`IdOverride.Trim()`), so *the plugin* does all validation:

* a positive integer, or `igdb:<n>`, or `game:<n>`;
* `https://www.igdb.com/games/<slug>`: parsed with `Uri`; scheme `https`; `Uri.Host` equal (case-insensitive) to `igdb.com` or `www.igdb.com`; no userinfo; port 443 or none; path exactly `/games/<slug>` (an optional trailing `/`, query and fragment ignored). The slug is accepted as `[a-z0-9-]+` (measured on 2,500 sampled slugs: only lower-case letters, digits and hyphen occur; the input is lower-cased first) and goes into the query only as an escaped constant-shape string (6.3). There is no SSRF route here: the plugin only extracts a slug and asks IGDB's API, never fetching the pasted address;
* `steam:<appid>` (digits only), resolved through `external_games` (6.1);
* anything else is refused with a message listing these forms. A `/characters/`, `/companies/` or `/platforms/` address is refused by name.

`GetAcceptedCrossRefPrefixes()` = `igdb:` and `steam:`, so a future Steam plugin that publishes `ids.steam` seeds IGDB with an exact lookup. Games have no family, so the plain list applies.

### 5.3 Cross-reference ids published: none

`ExtendedData.ids` is **not written**. Whatever is under `ids` seeds *other plugins'* enrichment rows (the Open Library lesson), and no games plugin exists yet to accept a Steam, GOG or Epic id in a known form. All of IGDB's external ids go under `extendedData.external_ids`, where nothing acts on them. When a Steam plugin is built it adds `steam` to `ids` here, together with its own accepted prefix, in one change.

### 5.4 External id policy

No `IExternalIdPolicyProvider`: the ids are plain integers of one kind, shared with no other provider, so plain handling is correct.

### 5.5 One external id belongs to one item

A person who keeps the same game as two Chronicle items (a PC copy and a PS5 copy) gets the second refused as "likely duplicate". The mechanism that allows two items of the **same** type to share a provider id is the host setting **`enrichment.shared_id_scopes`** (`SharedIdScope{MediaType, Level, RequireSameName, RequireSameArtist}`; set `RequireSameArtist` false for games). It is **not** `enrichment.same_work_media_types`, which only relates two *different* types (books/audiobooks). The design's answer: one Chronicle item per game, with the platform a property of the person's play. Turning the other behaviour on means writing an `app_settings` row (a migration seed or the settings API), which is a host change, so it is decision 5, not part of the plugin.

## 6. Matching

### 6.1 Order of attempts

1. **Known `igdb` id** -> `where id = N` (score 100, `IdentifierMatch`). A genuine empty array means the id is gone: fall through to the name route.
2. **Known `steam` id** (digits) -> one request on `external_games` expanded to the game: `where external_game_source = <steam source id> & uid = "<appid>"`. The source id is read once from `external_game_sources` (the entry named "Steam", case-insensitive; cached 24 hours; one request). **If that table cannot be read, the Steam route is skipped** (no deprecated `category` fallback is ever sent). The returned `uid` must equal the requested appid. Several IGDB games can share one Steam uid (bundles, DLC): **prefer the main game type**, then the earliest release. A miss falls through.
3. **Name search** (6.3).

### 6.2 What a record is

The record's `game_type.type` (a string, expanded inline) classifies it. The plugin never uses the numeric enum and never filters by id in the query, so a renumbering costs nothing. Classification is by lower-cased name, the sets being settings (8):

* **Matchable as the game itself:** Main Game, Remake, Remaster, Expanded Game, Port, Standalone Expansion. (Measured: `game_types` holds exactly these 15 strings, ids 0 to 14: Main Game 0, DLC 1, Expansion 2, Bundle 3, Standalone Expansion 4, Mod 5, Episode 6, Season 7, Remake 8, Remaster 9, Expanded Game 10, Port 11, Fork 12, Pack / Addon 13, Update 14. **Main Game has id 0**, so no code may treat an id of 0 as "missing".)
* **Matchable only on an exact title** (penalty, 6.4): DLC, Expansion, Bundle, Fork, and any record with a `version_parent`.
* **Also matchable on an exact title only, with the same penalty (nothing is excluded):** Mod, Episode, Season, Pack / Addon, Update. A person who tracks a mod or an update pack can still match it by its exact title; what is refused is a *similar* title.
* An unrecognised type string is treated as "exact title only" and logged once per process: a new IGDB type neither disappears nor wins.

### 6.3 Building the query

**What the host actually sends** (checked): the enrichment pass sends `Name` as the item's **raw** name (not normalised, despite the record's doc comment), the year, and `AltTitles` whose first entry is the host-cleaned name; **`PreciseName` is never set by the host**. Add Media and the file-scan search send only `new MediaSearchContext(query, MediaTypeName: hint)`: **no year, no alternative titles**. So the query is `AltTitles[0]` when present, else `Name`; `PreciseName` is read if a future host sets it. The year signal exists only in enrichment.

**Cleaning** (the normaliser family of Open Library, Deezer and TheAudioDB): fold accents; fold typographic characters to ASCII (dashes to `-`, curly quotes to `'` and `"`, non-breaking space to space); remove (TM) (R) (C) signs; `&` equals `and`; drop a leading article; drop everything that is not a letter or digit for comparison. A trailing `(YYYY)` is the year hint and is removed. A trailing bracketed or dash-separated token that matches a platform name or abbreviation of the *candidate* (`[PS4]`, `(PC)`, `- Switch`) is a platform hint and is removed. A `?` or U+FFFD is left **out** of the query, not replaced. **An empty or blank name makes no request.**

**Numerals.** Roman numerals II to IX and XI to XX fold to digits (so "Final Fantasy VII" equals "Final Fantasy 7"). **A lone `I`, `V` or `X` is never folded when it is the last word or the first word**, because "Mega Man X" is a different game from "Mega Man 10" and "I Am Alive" is not "1 Am Alive". An equality that needed folding scores **lower** than a literal one (6.4), so the year decides when both exist.

**Query text (security).** The text goes into an Apicalypse string. Measured: an escaped quote does not break the string, an unterminated quote is a 400 `Syntax Error` whose hint names backslash-quote as the usual cause, and a hostile string containing a second clause was treated as plain search text (0 results, no injection). IGDB's behaviour is therefore benign, but the design still **relies on no escaping rules**: `"`, `\`, `;`, every control character and every newline are **removed** from the search text, the text is capped at 200 characters, and nothing else from the item ever reaches the body. A name like `x"; where id > 0; fields *; limit 500; "` therefore becomes one harmless search string (tested). The other values placed in a body are validated first: ids are positive integers formatted invariantly, the Steam appid is digits only, a slug passes the character check in 5.2.

**The search strategy was changed by the live probe (P1, P12).** IGDB's fuzzy `search` ranked the exact title first for only 22 of 43 titles and in the top 10 for 37; it missed "Control" and "Journey" entirely (they appeared at 13 and 16 of 50), and "Fallout" never, because IGDB names the original "Fallout: A Post Nuclear Role Playing Game". A case-insensitive **exact-name query** found the right game first in 14 of 18 follow-up cases with only 1 to 10 results: `where name ~ "<title>"; sort total_rating_count desc;` (equality, not prefix). IGDB also stores the common spellings as alternative names, and `where alternative_names.name ~ "<title>"` found "Final Fantasy 7", "Baldur's Gate 3" and "Civilization VI" first when neither the name query nor fuzzy search did. So the order is: **(a) exact name, case-insensitive; (b) exact alternative name; (c) fuzzy `search`**, stopping at the first that yields a candidate passing the gate, with (a) and (b) sent as **one request** (measured, P12b: `where (name ~ "x" | alternative_names.name ~ "x")` returns "Final Fantasy VII" (6 results) for "Final Fantasy 7", "Baldur's Gate III", "Sid Meier's Civilization VI", "Control", "Hades", and the original Fallout under its full title). **Interactive** callers (one request allowed) send (a); the enrichment pass may follow with (b) and (c). Because the exact-name result set is small and sorted by popularity, the scoring below stays as designed, and the numeral folding below becomes a *query variant* (try the other numeral form in (a) and (b)) as well as a comparison rule.

**Variants.** (1) the cleaned title; (2) only if (1) found no acceptable candidate and the title has a subtitle, the **main title** (before the first `:` or ` - `); (3) the other form of a leading "The". **Edition words** (default: *Game of the Year, GOTY, Deluxe, Ultimate, Complete, Definitive, Gold, Collector's, Digital Deluxe, Anniversary, Standard, Premium, Enhanced*) are removed from either side before comparing, **whether in brackets, after a dash, or trailing with no separator** ("Dishonored Definitive Edition"). A qualifier that is a different release is never removed: *Remastered, Remake, HD, Redux, Reloaded, Director's Cut, Part, Episode*, a number, a Roman numeral ("Resident Evil 2" and its 2019 remake are separate IGDB records; "Hades" is not "Hades II"). Both lists are settings.

**Interactive versus enrichment.** `MediaSearchContext.IsRealHierarchyPosition` is true **only** for the enrichment pass (checked: the one place it is set). When false the plugin makes **exactly one** request (the exact-name query, which returns complete records) and applies no variants; when true it may make up to two searches.

The request: the query in the order above, `fields *` plus the expansion groups of 9.7, `limit 10` (`search_limit`). Measured: `where` may follow or precede `search`, and (a) and (b) above are preferred to it.

### 6.4 Scoring (0 to 100; the host's gate is 50)

Raw score and cap are kept separately, and no later bonus lifts a cap (the Deezer lesson).

| Rule | Points |
|---|---|
| Title equal after cleaning, literally | 60 |
| Equal only after numeral folding | 52 |
| Equal after removing edition words from either side | 55 |
| An alternative or localized name equal | 50 |
| Equal main title (before `:`/` - `) only | 40, and **capped at 45 when the candidate has a subtitle the library title lacks, or the library has one the candidate lacks** (so "Fallout" never reaches the gate as "Fallout: New Vegas") |
| Anything else | **discarded** (never match a similar name; a title merely appearing inside another is not a match; a title that runs on after a space, "Get Born Live", is another release) |
| Library year within 1 of the first release year (UTC) or of any release-date year | +10 |
| Library year known and more than 2 away from the first release **and** every release date | **cap 40**, unless the library title carries an edition word or the candidate is a remaster/remake/port |
| Platform hint matches a candidate platform | +8 (no penalty if not: IGDB's lists are incomplete) |
| Type: main game +5; remake/remaster/expanded/port/standalone 0; DLC/expansion/bundle/fork/version -15 (not applied when the library title says DLC, Expansion, Bundle...) | |
| Popularity (`total_rating_count`), a tiebreak between same-name games | up to +8 (log scale), never lifting a cap |

A name-only exact match with the year agreeing is about 83 to 91 and passes; a same-named game from the wrong year is capped below the gate. At most five candidates are returned.

### 6.5 What the library holds is not used to choose

Flat items have no children or siblings; the year, the platform hint and popularity are all the corroboration. **A title absent from IGDB is NotFound, which is permanent until a person resets it** (the Enrichment drill-down's reset; the host's retry window applies to Failed rows, not NotFound). Fix match is the remedy.

### 6.6 Request budget

Counted per **plugin method**, because the host calls two:

* `SearchAsync`, **enrichment pass**: (1) the exact-name-or-alternative-name query, **lean** (2 KB), which gives candidates to score; (2) only if nothing passes the gate, the fuzzy `search`, lean; (3) **one multiquery for the chosen candidate**: the full game record, its time to beat and its characters, so the record returned is complete; plus at most one field-drift retry. So 2 requests in the common case, at most 5.
* `SearchAsync`, **interactive** (Add Media and scan search, 3.5 s): **exactly 1** request, the lean exact-name-or-alternative query. Nothing else; a person's later *add* calls `GetByIdAsync`, which does the full fetch.
* `GetByIdAsync`: the same single multiquery (game, time to beat, characters) for the id, **0 requests** if the record was already fetched in full by the search and is still cached. A known `igdb` id on the enrichment path costs this one request and no search.
* One enrichment is therefore typically **2 requests** and always ends with a complete record in hand before the host's follow-up call, so the host swallowing a failed follow-up (9.1) loses nothing. The contract tests assert these numbers.

## 7. Field mapping

### 7.1 Complete records, fetched once

The host's enrichment calls `SearchAsync`, then `GetByIdAsync` for the chosen id, and **if that follow-up fails it swallows the exception, saves the item with the search record, and does not revisit it** (checked). Measured costs of the alternatives: a lean search of 10 candidates is 2 KB (0.11 s); the same search with every expansion is **565 KB (0.51 s)**; one full record (`fields *` plus every expansion) is 20 to 115 KB and about 0.25 s (Fallout: New Vegas, with 196 keywords, is the large end). So the design does **not** expand every candidate, and does **not** leave the full fetch to the swallowed follow-up:

* Candidates are found **lean**, scored, and the winner is **fetched in full inside `SearchAsync`** with its extras in one multiquery (6.6). The record the host receives is complete, and `GetByIdAsync` is then a cache hit.
* The full record is **`fields *` plus the expansion groups of 9.7** (measured: `fields *, x.*, ...` returns every key `*` returns, 44 of 44, plus the expanded objects), the **time to beat**, and the **characters** (36 for that game; the multiquery returned all three in 0.16 s).
* **Stored once, not twice.** `ExtendedData` holds the complete expanded record in its structured form; there is no second raw copy beside a mapped copy. The mapped fields (title, genres, images...) are derived from it. A field IGDB adds tomorrow is inside it already.
* Nested rows' own `checksum`, `created_at` and `updated_at` are about a third of the bytes of an expanded record; under the lossless rule they are kept (decision 10 offers dropping only those three).
* Interactive callers get lean candidates only (title, summary, year, cover, platforms, type), enough to list and pick; the full record is fetched when one is chosen.

### 7.2 Mapping

| Chronicle field | IGDB source |
|---|---|
| Title | `name` |
| AlternateNames | **every** `alternative_names.name` whose comment does not contain `executable` (case-insensitive; measured: IGDB mixes executable file names into this endpoint, comment `Windows Executable`, in two spellings of the case). **The executable entries are not dropped: they go to `extendedData.executable_names`.** Other comments seen (all kept): Alternative title, Stylized title, Acronym, Abbreviation, Alternative spelling, Working title, language titles. Plus every localized name from `game_localizations.name` and the `Title: Subtitle` forms |
| Overview | `summary`, cleaned (7.3). `storyline` is kept whole in extended data (often a spoiler) |
| Year | the year of `first_release_date` (UTC); absent for a game with no date |
| SourceUrl | `url` (the igdb.com page). No `sourceUrls` rule in the manifest: IGDB pages need the slug, which an id-based template cannot rebuild |
| `poster_url` | the cover at `poster_size` (default `cover_big_2x`) |
| `backdrop_url` | the first artwork at `t_1080p`; failing that the first screenshot; absent if neither (never invented). The *rest* are not discarded: see the next row |
| Additional images (typed) | **all** of them, at the largest useful size (`t_1080p`; the host's own checker verifies each address, and measured fetching of 60 images took 1.6 s): every screenshot (type `Screenshot`), every artwork (type `Fanart`, which the frontend already maps to the backdrop slot), every cover including the localized covers from `game_localizations` (type `Front`), and every character mug shot (type `Character`). No count is bounded. Company logos and other images that have no display slot are kept as address and size in extended data (below), not as additional images, because an unknown type lands under Posters and would bury the cover |
| Genres | `genres.name` |
| Tags | themes, game modes, player perspectives and **every** keyword, plus `Series: <name>` for each series (12.5) |
| Rating | the legacy single `Rating` on the **0 to 10** scale (the scale TMDB, IMDb and TheAudioDB store), from `total_rating` by default (`rating_source`), whatever its vote count: **a score is never hidden** (the vote count is stored beside it, and `minimum_rating_votes` defaults to 0 for a person who wants a floor). No score at all means no rating, never 0 |
| RuntimeMinutes | absent unless `use_time_to_beat_as_runtime`: then `normally` seconds / 60 |
| Cast, Crew | none |

`ExtendedData` (lossless, camelCase): `partial`, `ratings` (a list shaped like the future `ProviderRating` contract, which the ratings project has designed but **not built**: `{key:"igdb", variant:"users"|"critics"|"total", value, scale:100, votes}`), `slug`, `igdb_url`, `game_type`, `game_status`, `version_parent`/`version_title`/`parent_game` (id and name), `release_dates` (per platform and region with the human text and date format, so "TBD" and "2026 Q3" survive), `platforms` (name, abbreviation), `companies` (`developers`, `publishers`, `porting`, `supporting`; for each company its id, name, description, country, start date, status, size, parent, websites and **logo address and size**), `franchise`, `franchises`, `collections`, `related` (`dlcs, expansions, standalone_expansions, expanded_games, remakes, remasters, ports, forks, bundles`, and **every** `similar_games` entry; id and name each), `game_modes`, `player_perspectives`, `themes`, `keywords` (full list), `game_engines`, `multiplayer_modes`, `age_ratings`, `language_supports`, `websites` (url, type name, trusted), `videos` (name, the `video_id` and the YouTube address built from it; stored as links, never embedded or fetched), `external_ids` (5.3; **every** external game, grouped by source: the uid, url, name, year, platform and release format), `time_to_beat` (seconds and `count`), `characters` (every character that lists this game: id, name, akas, description, gender, species, country, and the mug shot address), `executable_names`, `executables` (the Game endpoint's own field), `hypes`, `follows` (deprecated, unreplaced, requested in an optional group), `created_at`, `updated_at`, every image id with its width, height, animation and alpha flags, and **`raw`: the complete game record exactly as IGDB returned it** (`fields *` plus the expansions), so a field IGDB adds tomorrow, or one this mapper does not name, is already stored.

**Nothing IGDB returns for a game is left out.** The only things not stored are what the service never sends (anything keyed to a user).

**Not done, deliberately:** the library's own wording of the title is **not** added to AlternateNames. The host's name-overlap gate and alias recording apply only to scanner-created items and are skipped for flat types, so it would only store an invented name.

### 7.3 Cleaning

`\r\n` to `\n`; non-breaking and doubled spaces collapsed; paragraphs kept; Markdown emphasis stripped to plain text. `http://` image addresses become `https://`; the protocol-relative `//images.igdb.com/...` the API returns is never used as is. Empty strings, `0` for a missing date, and `null` are absent.

### 7.4 Reading responses

**Measured trap:** the Main Game type has `id` **0**, and several nested objects carry ids. A reader that tests an id for truthiness (or `> 0`) drops every main game's type. Presence is tested by `ValueKind`, never by value. Every read uses `TryGetProperty` and tolerant accessors (`ValueKind` checked before `GetString`, `GetInt32`, `GetDouble`). `GetProperty` and an unchecked accessor throw `KeyNotFoundException` or `InvalidOperationException`, and the host maps those to *clear the stored id* and *Skipped* respectively (9.1): a parsing slip would silently turn into a permanent state. A response whose shape is not the expected one (not an array, an element that is not an object, an `id` that is not a number) is a **null-status `HttpRequestException`** (9.2), never a not-found. Tests feed `null`, a wrong type and a missing property to every field.

## 8. Settings

Everything a person may tune is a setting, except operational limits of the plugin's own machinery (the breaker's cool-down lengths, the cache lifetime and size), which are named constants documented here as technical limits in the sense of the hardcoding audit.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `notice` | Notice | - | How to get the two values: a Twitch account with two-factor authentication, an application at dev.twitch.tv/console (Confidential, redirect `http://localhost`), **New Secret**; non-commercial use; **use a separate Twitch application for each Chronicle instance** (10.6) |
| `client_id` | Text | empty | |
| `client_secret` | Password | empty | Encrypted at rest by the host. The Plugins page shows saved settings to administrators, so the secret is visible to them; it is never logged or placed in a URL (10.5) |
| `requests_per_second` | Number | `3` | 1 to 4 (IGDB's limit is 4) |
| `max_concurrent_requests` | Number | `3` | 1 to 8 (IGDB allows 8 open) |
| `request_timeout_seconds` | Number | `8` | One request. 2 to 20 |
| `call_deadline_seconds` | Number | `20` | One plugin call, kept under the host's 25-second guard. 5 to 24 |
| `search_limit` | Number | `10` | Candidates per search, 1 to 50 |
| `poster_size` | Dropdown | `cover_big_2x` | Cover size from the documented list |
| `rating_source` | Dropdown | total | total / users / critics |
| `minimum_rating_votes` | Number | `0` | A floor a person may set; the default hides nothing. Fewer votes than the floor: the score stays in extended data only |
| `fetch_time_to_beat` | Boolean | **true** | Part of the full fetch (7.1); turn off only to save a request |
| `use_time_to_beat_as_runtime` | Boolean | false | Fill `runtime_minutes` from "normally" (decision 6) |
| `edition_words`, `never_strip_words` | TextArea | the lists in 6.3 | One per line |
| `exact_title_only_game_types` | TextArea | 6.2 | One per line, matched to `game_types.type` ignoring case. A listed type is matched only on an exact title and carries the type penalty; **no type is excluded** |
| `platform_aliases` | TextArea | empty | `ps5 = PlayStation 5` lines |

User-Agent `Chronicle-IGDB/<version>`. Settings are read defensively: an unparsable or out-of-range number falls back to its default and is logged once; `Configure` with an empty dictionary does not throw and does no awaited network work.

## 9. What the host does, and therefore what the plugin throws

### 9.1 The host's behaviour, per caller (read from the source)

The same exception means different things on each path. Verified:

| Caller | Exceptions from `SearchAsync` / `GetByIdAsync` |
|---|---|
| **Enrichment pass** (`EnrichItemCoreLockedAsync`, MetadataEnrichmentService.cs ~2252-2356) | `PluginAuthException` -> row `AuthFailed`, **the pass continues** with the next item. `HttpRequestException` 429, or **null status**, -> row left Pending, `RetryCount` untouched, `ProviderUnavailableException` stops this plugin's pass. `HttpRequestException` 404 -> NotFound. `InvalidOperationException` -> **`Skipped`, terminal** until a person resets Skipped. Any other exception (including 5xx, timeouts, `TaskCanceledException`) -> `RetryCount++`, `Failed`, and **`Exhausted` at 3**. An empty result -> **NotFound, terminal** ("a valid terminal state"; only the manual reset undoes it). |
| **Follow-up `GetByIdAsync` after a search** (~2007-2025) | **Every exception except cancellation is swallowed** ("keeping search result"): the item is saved with the search record and is not revisited. A 429 here does **not** pause. |
| **Stored-id fetch** (~1605-1640) | `KeyNotFoundException` or `ArgumentException` -> the stored id is cleared and a fresh search runs. `HttpRequestException` 404 -> NotFound. |
| **Add Media search** (`FileScanService.SearchMetadataAsync`, 4 s guard) | A bare `catch` returns an empty list: **auth failure, outage and breaker all look like "no results"**, silently. Carries no year. |
| **Add Media add** (`AddFromSearchAsync`) | Any `HttpRequestException`, `KeyNotFoundException`, `ArgumentException` becomes "could not find {id}: it may be stale or invalid". `PluginAuthException` is not caught. |
| **Refresh / Fix Match** (single item) | `ProviderUnavailableException` is private to the batch loops; `MediaController.RefreshMetadata` catches everything and returns 502 with `ex.Message`. **The message text reaches the user**, so every message the plugin throws is user-grade and contains no secret. |
| **`ProviderCallGuard`** (every plugin call) | Each attempt has a fresh 25 s timeout (on timeout it returns the fallback, which enrichment reads as **NotFound**, terminal; hence the plugin deadline below 25 s is a hard requirement). It retries null-status, 429, 502, 503 and 504 **twice**, sleeping 2 s then 4 s on the caller's token only, so a pause-type failure costs a caller up to about 6 s of waiting however fast the plugin fails. |
| **Resync-all** (`ResyncAllForPluginAsync`) | No catch: the first pause-type failure aborts the whole task, and the early 15-minute retry applies only to `fetch-missing-metadata`. A resync restarts from the first item next time. It forces a fresh search (clearing the stored id) and relies on `KnownExternalIds["igdb"]` to short-circuit it. |
| **Item lock** | 40 s held across the whole item (search + follow-up + guard retries); a concurrent Refresh can get "lock timeout, skipping". |

### 9.2 The mapping

Every failure is mapped on purpose, and the **interactive** path (`IsRealHierarchyPosition` false) differs from the enrichment path:

| What went wrong | Enrichment path / `GetByIdAsync` | Interactive search |
|---|---|---|
| HTTP 429 | `HttpRequestException(429)` | return an **empty list**, log (the host's guard would otherwise sleep 6 s and then show nothing anyway) |
| Timeout, DNS, refused, TLS, **5xx**, endpoint 404, **a body that is not the expected JSON shape** | `HttpRequestException` with **`StatusCode = null`** and a clear message: a pause, no retry burned. (5xx as itself would reach the host's generic branch and burn `MaxRetries`: three bad minutes at IGDB must never exhaust a library.) | empty list, log |
| Credentials missing | **`PluginAuthException`** ("IGDB needs a Twitch client id and secret...") | empty list, log |
| Credentials rejected (Twitch 400/403 on mint, IGDB 401 after one re-mint, IGDB 403) | `PluginAuthException` naming the fix, **and the credentials latch (9.3) is set** | empty list, log |
| Genuine 200 with `[]` for a search | empty result (NotFound) | empty list |
| Genuine 200 with `[]` for an id | `KeyNotFoundException` (so a stored id is cleared and a search runs; Fix Match and Add Media report it as a dead id) | n/a |
| HTTP 400 on a **search** | the field-drift retry (9.7); if the core-only request still fails, **throw the null-status `HttpRequestException`** (a pause), **never an empty list**: a deprecated field must not turn the library into permanent NotFound | empty list, log |
| HTTP 400 on an **id fetch** | null-status `HttpRequestException` plus an error log | n/a |
| Per-item deterministic failure (9.4) | after the limit, `InvalidDataException` (burns `MaxRetries`, ends Exhausted, queue moves on) | n/a |
| Cancellation | `OperationCanceledException` | same |

### 9.3 The credentials latch (an auth failure does not stop the pass)

The host continues the pass after `AuthFailed`, so a rejected secret on a 10,000-item queue would otherwise cost one to two failing calls (a mint and an IGDB request) per item and 10,000 `AuthFailed` rows. The plugin keeps an in-memory **latch** keyed by a hash of (client id, secret): once credentials are missing or rejected, every later call throws `PluginAuthException` **immediately, without any network request**, until `Configure` receives different values. The same latch covers a second 401 from IGDB. **Unverified (host):** whether the `AuthFailed` rows already written are re-queued automatically when new settings are saved, or need the Enrichment page's reset for AuthFailed; check in the host source before building and document whichever is true in the README.

### 9.4 Poison items

A pause stops the whole pass, and rows are ordered only by hierarchy level, so an item that **deterministically** fails (a record that always returns 500 or malformed JSON) would be first again on every 15-minute retry and block everything behind it. The plugin keeps a bounded in-memory counter per (id or normalised query) for **per-item-deterministic** failures only (5xx on an id fetch, malformed shape, 400 on an id fetch; **not** 429 and **not** transport errors, which are not about the item): after 3 consecutive failures for the same key it throws `InvalidDataException` instead, which the host counts toward `MaxRetries`, ending in `Exhausted`. The counter resets on success and is bounded (oldest dropped).

### 9.5 One limiter for everything

A single limiter per plugin instance is shared by every caller: a **minimum interval of `1 / requests_per_second`** between request starts and at most `max_concurrent_requests` in flight. A caller waits only as long as its cancellation and the call deadline allow; if the wait would pass the deadline it throws the 429 form ("IGDB pacing queue full"), **not** a timeout, so a busy queue pauses instead of failing items. The pacing figure is a floor on the interval, not throughput; real throughput depends on latency (P11).

### 9.6 Timeouts, in layers

* Each request: `request_timeout_seconds` (default 8), enforced by the plugin's own linked cancellation, not only by `HttpClient.Timeout`. Automatic redirects are **off** on every client.
* One plugin call: `call_deadline_seconds` (default 20), under the guard's 25 s. When it elapses the plugin throws the null-status form, so a hang is a pause and never the guard's fallback (which would be NotFound).
* Interactive search: the host allows 4 seconds; the plugin's deadline there is 3.5 s, one request, no variants, token already warm.
* One long-lived `HttpClient`, `SocketsHttpHandler` with a few minutes of `PooledConnectionLifetime`. `Authorization` is set per request, not as a default header, so a rotated token takes effect at once.
* **The plugin never retries on its own.** The host's guard already retries pause-type failures twice; a second layer would multiply the wait.
* The 40-second item lock is held across search, follow-up and guard retries; the design keeps a whole item under it by keeping each call under 20 s and the worst case at two guard retries on a fast failure.

### 9.7 Field drift (IGDB deprecates fields)

A request naming a field IGDB no longer has fails with HTTP 400 (measured, P6): the body is a JSON array `[{"title":"Invalid Field","status":400,"cause":"Invalid field name: 'x'"}]`, so the plugin **parses the field name out of `cause`** and drops exactly the group that contains it. The field list is `fields *` (every field, which can never name a missing one; measured to combine with expansions and lose no key) plus **expansion groups**: core (`cover.*, genres.*, game_type.*, platforms.*`), media (`artworks.*, screenshots.*, videos.*`), companies (`involved_companies.*, involved_companies.company.*, involved_companies.company.logo.*`), names (`alternative_names.*, game_localizations.*`), releases (`release_dates.*`), commerce (`external_games.*, websites.*`), classification (`themes.*, keywords.*, game_modes.*, player_perspectives.*, game_engines.*, franchises.*, collections.*`), ratings (`age_ratings.*, age_ratings.rating_content_descriptions.*`), related (`similar_games.name, dlcs.name, expansions.name, ...`), languages (`language_supports.*`), multiplayer (`multiplayer_modes.*`). (Measured in P13: this exact form returned 200 for a real game, with company logos, localizations, age-rating descriptions and language-support types expanded.) On a 400 the plugin retries **once**, dropping the group the response names (or, if it names none, every expansion group, leaving `fields *`); remembers which groups failed for this process; logs it once with the field; and the health check says "degraded: group X disabled". The deprecated fields still returned by `fields *` are kept in `raw` like everything else, and are never *named* in a request; `follows`, deprecated with no replacement, is only ever present through `*`. A test greps every request body for a deprecated name. If even `fields *` alone fails, the answer is the pause form in 9.2, never an empty result.

### 9.8 The breaker (backing off)

On a **real** 429, 5xx or transport failure the plugin opens a cool-down; until it ends every call **fails immediately, without touching the network**, with the same exception (so the host's two guard retries cost two instant failures, not two more requests into a struggling service, though they still sleep 2 s and 4 s):

* 429: **2 s**, doubling per consecutive real failure to 5 minutes. Measured: IGDB's 429 body is `{"message":"Too Many Requests"}`, it sends **no `Retry-After` and no rate-limit headers**, and it answered 200 again **0.6 s** after a 150-request burst ended, so a 30-second first cool-down would be wasted time; the 2 s start also matches the host guard's first retry delay. A `Retry-After`, if IGDB ever sends one, sets the length (bounded to 5 minutes).
* 5xx or transport: 15 s doubling to 5 minutes.
* ±20% jitter.
* A fail-fast call does **not** extend or escalate it.
* The first success resets it. Authentication failures do not open it (the latch handles them).
* In memory, lost on reload. The health check reports "cooling down for 1m 40s after HTTP 429" without touching the service.

### 9.9 Schedule and cost

`fetch-missing-metadata` daily at **05:45** (enabled), `resync-all-metadata` weekly, Thursday **03:45** (**disabled**), the host's built-in handlers, offset from every existing plugin (TheAudioDB 05:35, Open Library 05:30, Deezer 04:50, Fanart.tv and Wikipedia 05:00, OMDb 06:10) because they share SQLite's single writer. A pause is retried 15 minutes later by the scheduler (`fetch-missing-metadata` only); a restart resumes interrupted tasks after 30 s.

**Cost, measured.** A lean search took a median 0.11 s (max 0.17); a full record with every expansion 0.25 s and 20 to 115 KB; the extras multiquery (game, time to beat, 36 characters) 0.16 s and 29 KB. One enrichment is a lean query (about 0.1 s) plus one multiquery (about 0.25 s): **2 requests, about 0.35 s of latency**, and about 30 to 120 KB downloaded and stored. The 3-a-second pacing is the binding limit, a floor of about 0.7 s per game; 12 searches at concurrency 2 or 3 took 0.75 s, so the service is not the bottleneck. 1,000 games take about 12 minutes and 10,000 about 2 hours, in the background, and a library of 10,000 games stores roughly 0.3 to 1 GB of IGDB data, which is the price of keeping everything (decision 10). Concurrency would not help while the pace is the limit, so the manifest leaves `max_enrichment_concurrency` at 1. A resync costs the same per game: the stored `igdb` id skips the query, so it is one multiquery each; because a resync aborts on the first pause and restarts from the beginning, it stays **disabled by default** and the README says so.

## 10. Authentication and the token

10.1 **Mint:** `POST https://id.twitch.tv/oauth2/token` with a form-encoded **body** (`client_id`, `client_secret`, `grant_type=client_credentials`) so the secret never sits in a URL; measured: Twitch accepts the body (200), so the query form is not used.

10.2 **Cache:** token and expiry in memory, reused until 24 hours before `expires_in` runs out (never below 60 s of life). Minting is **single-flight** (concurrent callers wait for one mint). `Configure` keeps a valid token if the credentials are unchanged and discards it when either changes.

10.3 **Warm-up:** `Configure` starts one best-effort background mint (errors swallowed, no secret logged, cancelled on dispose) so the first interactive search does not pay for it.

10.4 **Failure meanings:** see 9.2. IGDB 401 -> discard the token, mint once, retry the request once; a second 401 -> `PluginAuthException` and the latch.

10.5 **Secrets:** the client secret and the access token never appear in a log line, an exception message (which can reach the UI), a result, or a URL. Every string leaving the plugin passes one redaction function removing both; the client id is shown truncated. Tested with a sink that records everything. (The host's settings endpoint returns saved values to administrators, so the secret is protected at rest and in logs, not from administrators.)

10.6 **Token scarcity.** An application can have only 25 active tokens, and every plugin reload mints a new one because state is in memory. A development install and the live install **sharing one Twitch application** can therefore invalidate each other's older tokens; the notice tells the owner to create one application per instance. The token is never persisted by the plugin (a metadata provider has no data directory).

## 11. Caching

Ten minutes, in memory, bounded (a few hundred entries, oldest evicted; technical limits): search results by cleaned query; game records by id (a search result answers the follow-up for the game itself, 7.1); the Steam source id and platform lookups, 24 hours. Never serves a record past its lifetime, never caches an error, never caches an empty "not found" for more than a minute. `GetImageAsync` fetches one image on request, only from `images.igdb.com`: `https` only, the host compared as a parsed `Uri.Host` ignoring case, no userinfo, port 443 only, redirects off, the body capped while streaming. Images are never downloaded in bulk.

## 12. Making Chronicle ready for games (the places a new type must be added)

Each item says whether it lives in the plugin, the host, a script or the docs, and how it is verified.

**12.1 The plugin declares the type** (4). The host creates plugin-declared types in `SyncMediaTypesFromPluginsAsync`, whose **only caller is host startup**; installing or reloading a plugin does not call it. So **after installing IGDB, Chronicle must be restarted once** before the plugin is loaded and (if no game plugin has made it yet) the `game` type exists (in the dev database the IMDb plugin had already created it, on 2026-10-02), (README and Getting started text say so; optionally a host change to sync after install). Rows an administrator edited (`IsUserModified`) are never overwritten. Verified on a clean database across a restart.

**12.2 Catalog:** a `plugins.json` entry `{ "plugin_id": "chronicle.plugin.igdb", "github_repo": "thegoddamnbeckster/Chronicle.Plugin.IGDB", "tags": ["game", "games", "metadata"] }`. It is the only catalogue (embedded in the build, copied in the Dockerfile; `PluginCatalogSeeds.cs` is no longer a list). Run `CatalogEntryGuardTests`, which validates every entry. `supported_media_types: ["games"]` in the manifest feeds the Plugins page filter.

**12.3 Dev environment:** add to `scripts/RunTestEnvironment.ps1` and `scripts/Deploy-Plugin.ps1` (both list plugins by hand; `scripts/deploy-plugins.ps1` is a legacy script and is ignored). The first deploy needs the file hash approved (`dotnet run -- --accept-plugin-changes` or the Plugins page), then the client id and secret saved once.

**12.4 Frontend image slots:** `TYPE_TO_SLOT` in `src/Chronicle.Web/src/utils/imageExtractor.ts` knows `fanart` (-> backdrop) but not `screenshot`; an unknown type lands in **Posters**, burying the cover. Add `screenshot` -> `backdrop_url` plus a unit test. This is the one host code change.

**12.5 Collections and franchises:** the collection machinery acts only on types whose provider family is `movie`. Games have none, so IGDB **does not write `belongsToCollection`**: series and franchise go to extended data and the series also becomes a tag. Generalising collections to games is a host project (decision 3).

**12.6 Metadata Assignment:** `game` has no saved lists, so the first plugin with a value wins and IGDB is the only one. The README tells the administrator to add RAWG/Steam to the lists when they exist.

**12.7 Scanning:** the grouped scan sorts files by the scan hints on media types; a new type has none. Games are added through **Add Media** (and later the Steam/GOG importers). **Do not create a scan folder with the type `games`**: the Scan Folders screen does not validate the type, and the scanner would still classify only video and audio files. The README says so.

**12.8 Statuses and wording:** nothing to add (`typeWording.ts` derives "Plan to Play / Playing / Replaying" from `played`). Checked by a render in the dev environment.

**12.9 Stats and time spent:** "time spent" comes from `RuntimeMinutes` (`StatsService`, `ReportService`, `ScrobbleService`); `WatchDeriver` derives export events and does not use runtime. With `runtime_minutes` empty a game contributes none, which is honest; decision 6.

**12.10 People:** IGDB supplies none, so no People records are created.

**12.11 Database:** no migration for the plugin, the type or the data. (The shared-id behaviour of decision 5 would need an `app_settings` row.)

**12.12 Other declarations and docs.** Nothing to change: the Wikipedia plugin, the IMDb plugin and the eight game scaffolds all declare `game` already, and IGDB now does too. Docs: replace `docs/plugins/PLUGIN_IGDB.md`; add IGDB to `docs/PLUGIN_CATALOGUE.md`; `docs/FEATURES.md`/README. `BACKLOG.md`'s stale AniList entry was corrected on 2026-11-05.

**12.13 Wikipedia (owner decision, 2026-11-05).** Wikipedia already declares `game`, which is now also IGDB's type (21), so it needs **no declaration change**; **its data is treated as suspect until verified** (the guards below are still to be built, in the Wikipedia repository). This is a change to the Wikipedia plugin and its own release; **it is independent of IGDB**: either plugin works with the other absent, they may be built and released in any order, and Wikipedia may not be installed at all. When it is installed it sits **last** in every Metadata Assignment list for games (its default priority 90 already does that), so it only fills what no other source has. Wikipedia has produced wrong matches before (same-named films, people, seasons, yearly lists), and a game title is the worst case ("Doom", "Prey", "Control", "Portal", "Hades", "Inside", "Journey", "Limbo", "Fable", "Dune" are all also films, books, myths or words). What the plugin does today (read from `WikipediaScoring.cs`): the type keyword set is keyed by `game`/`video_game`, so the type IGDB shares gets the keyword "video game" (had the type been `games` it would have got none and lost its collision defence silently); and the conflicting-type check rejects an article only when its description names a *different* type, so an article with **no** description is accepted. The guards, all of which use **only the item and Wikipedia itself**:

1. **Positive identification, not absence of conflict.** For `game` an article must positively say it is a video game: the Wikidata description or the opening sentence contains "video game" (or the title carries a "(video game)" disambiguator). A missing or generic description is a **reject**, not a pass.
2. **Negative disambiguators are hard rejects:** "(film)", "(TV series)", "(novel)", "(band)", "(album)", "(song)", "(board game)", "(card game)", "(mythology)", "(character)", "(comics)", and a disambiguation page.
3. **Exact title only** after stripping a disambiguator and edition words; a similar title is never accepted.
4. **Year agreement** when the item has a year: the article's release year (opening sentence or infobox) against the item's year; a disagreement of more than 2 years is a hard reject; an article with no readable year is capped below the return threshold.
5. **Optional extra corroboration, never a requirement.** If the item happens to carry developer, publisher, platform or release data from any source (IGDB or otherwise), an article whose text contains none of them is capped below the threshold; if the item carries none, this guard does nothing and guards 1 to 4 decide alone. Wikipedia **never** supplies identity (it is `NameDerived`: agreeing on it proves nothing, and it is never merge proof).
6. **A Wikipedia link from another source is a hint, not proof.** IGDB lists a Wikipedia page among a game's websites (website type 3, measured), which is crowd-sourced. It may *prefer* one candidate among several that already pass guards 1 to 4, and is ignored when absent.
7. **It declares the fields it can fill and stores everything** (the lossless rule applies to Wikipedia too): `overview`, `poster_url`, `tags` and its extended data, **last** in the lists. Identity is secured by guards 1 to 4, not by withholding data from a verified article. An article that fails a guard yields no data at all.
8. **When in doubt, NotFound.** A wrong overview is worse than none. NotFound is terminal in the host (9.1), so the guards err toward rejecting the doubtful *and* the rejected candidates and reasons are kept in the host's enrichment diagnostics, so a person can see why and fix the match.
9. **Tests (fixtures of real articles):** every title in the list above against its film, book, myth and game pages; an article with no description; a "(film)" disambiguator; a year mismatch; with and without extra corroborating data (both must work); the hint-cannot-admit case; the plugin working with no other plugin installed.

## 13. Making the data show

1. **Declared fields use the host's exact names** (4), with a test against the host's field list.
2. **Assignment lists:** `DefaultPriority = 10`; the README says where it sits and how to move it.
3. **Every image, typed.** Cover -> `PosterUrl` (when IGDB has no cover but has a screenshot, the first screenshot fills `PosterUrl` so the item still has a picture). First artwork -> `BackdropUrl`. **All** screenshots, artworks, covers and character mug shots go in as typed additional images (7.2); nothing is bounded. The host HEAD-checks every address, gated at 4 concurrent per host, which at the measured 0.35 s per image is a couple of seconds for a large game.

## 14. Background tasks

See 9.9. Both are the host's built-in handlers; no `IPluginTask` is needed.

## 15. Tests (all required before release)

Recorded real responses go in `tests/fixtures/` after the prototype.

**Standalone (release blockers):** found by name alone with `KnownExternalIds` null, empty, and holding only unrelated keys; wrong-shape ids ignored (`steam` of `abc`, `igdb` of `-3`, `0`, a float, empty); the assembly references no other plugin; the manifest declares no dependency and no other plugin id appears in the assembly's strings; the media type declaration equals a **golden declaration** checked into this repo (the cross-plugin byte-equality is checked when a second games plugin exists); no `ids` key in any fixture; a full fake-HTTP run (search, fetch) with nothing else installed.

**Unit:** the normaliser (typography, `&`/and, articles, trademark signs, numerals: `Final Fantasy VII` = `7`, `Mega Man X` != `Mega Man 10`, `I Am Alive`, a folded-only equality scoring lower); `(YYYY)` and platform-tag stripping; edition words stripped including trailing no-separator ones, and **never-strip words kept** (Remastered, Remake, Redux, Director's Cut, Part 2; `Hades` != `Hades II`); main-title cap **in both directions** (`Fallout` vs `Fallout: New Vegas` and the reverse); scoring for every row of 6.4 with caps never lifted by popularity; type classification with an unknown type; id and URL parsing (hostile hosts such as `igdb.com.evil.test`, userinfo, other ports, a character page, a bare word); the Steam uid agrees and prefers a main game; field mapping for every row of 7.2; the rating scale and vote threshold; address building (no protocol-relative output); text cleaning; settings parsing (garbage -> default, logged once); **reader tolerance** (`null`, wrong type, missing property in every field -> no `KeyNotFoundException` and no `InvalidOperationException`, and a wrong overall shape -> the null-status exception).

**Contract (fake handler):** the per-method budgets in 6.6 (interactive search exactly 1; empty name 0); the query body for hostile names is one `search` string and nothing else (quotes, backslash, semicolons, newlines, 5,000 characters); **429 thrown, not waited out; the breaker** (opens on real failures, fails fast with no request, does not escalate on fail-fast calls, doubles to the cap, resets on success, honours `Retry-After`, jitter bounds, not opened by auth failures; fake clock); **timeouts** (a never-answering handler ends the request at `request_timeout_seconds` and the call at `call_deadline_seconds` as a null-status exception; the interactive path ends within 3.5 s); **the mapping tables 9.2 row by row, for both paths**; the credentials latch (after a rejection no further request is made until different settings arrive; missing credentials throw `PluginAuthException` with no request); poison-item counter (3 consecutive deterministic failures -> `InvalidDataException`, reset on success, 429 never counts); limiter shared by concurrent callers, pacing at 1, 3 and 4 a second, queue-full is a 429 not a timeout; **token** (one mint under 20 concurrent callers; reused until near expiry; a 401 re-mints once then latches; Twitch 400/403 are auth errors; the secret is nowhere in any URL, log, message or exception, searched with a recording sink); **field drift** (a fake 400 naming each optional group is survived once and remembered; a 400 on the core group is the pause form, never an empty list; no deprecated field in any body); caching (a lean record never answers `GetByIdAsync`; errors not cached; expiry); `GetImageAsync` refuses another host, a redirect, userinfo and a non-443 port; a game with no cover, date or summary maps without exceptions.

**Host tests (Chronicle repo):** the `TYPE_TO_SLOT` change; loading a plugin that declares `game` creates the row once across a restart and a second declaration changes nothing; the Assignment page lists `igdb` for `games` with exactly the declared fields; `CatalogEntryGuardTests` with the new entry.

**Live tests** (off unless `IGDB_LIVE=1` with `IGDB_CLIENT_ID` and `IGDB_CLIENT_SECRET`; never in CI): a fixed list of real games including a remake and its original, a sequel with a Roman numeral, a subtitle, a GOTY edition, a DLC, an unreleased game, a delisted game; pacing respected.

**Manifest test:** id, version, tasks, supported types, declared fields and settings match the code; every key `Configure` reads is in the schema.

## 16. Release and docs

README in the standard format (badge, Plugin ID / Version / Auth block, supported type and fields, id forms, how matching works, every setting, tasks, the **restart-once** and Assignment instructions, the "do not make a games scan folder" warning, the free-tier limits, the non-commercial wording, structure, building). `manifest.json`: `entry_type`, `iconUrl` (IGDB's favicon, checked at build time), brand colours (checked for contrast in both themes, P10), `fixMatchHint` ("Paste an igdb.com game page, an IGDB id, or steam:<appid>"), `supported_media_types: ["games"]`, **`min_chronicle_version` 0.7.0** (the scaffold says 0.6.0; the design needs `IsRealHierarchyPosition`, `IdentifierMatch` and the plugin-declared type fields), the two tasks, `max_enrichment_concurrency` after P11. **csproj:** `Private="false" ExcludeAssets="runtime"` on the `Chronicle.Plugins` reference, the Serilog pin, tests excluded from the plugin build (the scaffold's csproj has none of these, and without them the zip would carry `Chronicle.Plugins.dll`, which the guide forbids). Release: one zip (DLL, deps.json, manifest), tag equal to the manifest version `1.0.0`; then the `plugins.json` and script entries (12.2, 12.3).

## 17. Prototype plan and results

**Done 2026-11-05 (17.1).** Needed the owner's Twitch client id and secret (a few minutes at dev.twitch.tv/console; the owner signs in, because sign-in and the secret are theirs), against a **disposable** application. The probe is a throwaway script outside the repo that reads `IGDB_CLIENT_ID` and `IGDB_CLIENT_SECRET` from environment variables set in the owner's own terminal: the secret is never typed by the assistant, never written to a file, the repo or a log. No plugin build is needed first. Record each response as a fixture.

| # | Question | Decides |
|---|---|---|
| P1 | Does `search "<q>"; fields ...; limit 10;` on `/games` rank an exact title first for 40 sampled titles? Does `where` change it? | 6.3 |
| P2 | Does the token endpoint accept a form body? What do a bad id and a bad secret return (status and text)? | 10.1, 9.2 |
| P3 | Is `search` + `where` + `limit` accepted together, in either order? | 6.3 |
| P4 | Does a 429 carry `Retry-After`? Does a multiquery count as one request? Is 4 a second enforced (burst of 12)? | 9.5, 9.8, time to beat |
| P5 | The real strings in `game_types`, `game_statuses`, `external_game_sources` (is "Steam" there?) | 6.1, 6.2 |
| P6 | What does an unknown field return (status and body)? What does a request for a non-existent id return? | 9.7 |
| P7 | Fill rates of each field on 100 well-known and 100 obscure titles and the awkward cases in 15 | 7, 13 |
| P8 | Behaviour and speed of `images.igdb.com` for 50 images in parallel; does `t_1080p` upscale a small cover? | 13 |
| P9 | Platform names and abbreviations returned; do `[PS4]`-style tags match them? Which characters occur in slugs? | 6.3, 5.2 |
| P10 | Contrast of the brand colour in both themes | 16 |
| P11 | **Latency** of a lean search, a full fetch and a time-to-beat call, sequentially and at concurrency 2 and 3; payload size of a lean and a full record | 9.9, 7.1, concurrency, interactive 3.5 s |

### 17.1 Results (live probe, 2026-11-05)

Run with a disposable Twitch application and the owner's credentials in environment variables; responses (credentials redacted) are recorded as fixtures in `Chronicle.Plugin.IGDB/tests/fixtures/live-2026-11-05/`, and the two probe scripts are in `Chronicle.Plugin.IGDB/tools/`. Sample: 43 titles (including the collision-prone ones), 25 famous and 25 recently added obscure records, 60 images.

| # | Result |
|---|---|
| P1, P3 | `search` ranked the exact title **first for 22 of 43**, in the top 10 for 37 (misses: Control, Journey, Fallout, "Final Fantasy 7", "Baldur's Gate 3", "Civilization VI"). `search` + `where` is accepted in **either order** (43 of 43 each). An exact-name query (`where name ~ "x"`) fixes most misses (6.3). Search does return the right record under Roman/Arabic and "Sid Meier's" variants only via **alternative names**, which exact-alt-name queries find. |
| P2 | The token endpoint **accepts a form body** (200). `expires_in` 5,417,814 s (62.7 days). A bad secret is 403 `{"status":403,"message":"invalid client secret"}`; a bad client id is 400 `invalid client`; a bad bearer token at IGDB is **401** with a "Tip 1/2/3" text body. |
| P4 | A 12-request parallel burst and five 10-query multiqueries all returned 200. A **150-request burst at about 80 a second drew 32 429s** (118 OK). The 429 body is `{"message":"Too Many Requests"}`; **no `Retry-After`, no rate-limit headers**; the service answered 200 again **0.6 s** after the burst ended. So the limiter is a bucket with a generous burst, not a hard 4 a second; a sustained 3 a second is far inside it. |
| P5 | The lookup tables: 15 game types (6.2), 8 game statuses (Released 0, Alpha 2, Beta 3, Early Access 4, Offline 5, Cancelled 6, Rumored 7, Delisted 8), 22 external game sources (**Steam id 1**, GOG 5, Epic Games Store 26, Amazon 20, Microsoft 11, GiantBomb 3, and one named IGDB, 121), 23 website types (**Wikipedia id 3**, Official Website 1, Steam 13, Epic 16, GOG 17, Subreddit 14, Community Wiki 2). |
| P6 | Unknown field: **400** with a JSON array `[{"title":"Invalid Field","status":400,"cause":"Invalid field name: 'x'"}]`, so the field name can be parsed out for drift recovery (9.7). A missing id: **200 `[]`**. The deprecated `category` and `status` still work (200) today. Unterminated quote: 400 `Syntax Error`. Garbage body: 400. A GET: 200 (ignored). An unknown endpoint: 404. An escaped quote and a hostile string: 200, no injection. |
| P7 | Fill rates, 25 well-known games: summary 24, cover 24, first release date 24, genres 25, themes 25, game modes 24, player perspectives 24, screenshots 24, artworks 23, keywords 24, videos 22, age ratings 23, rating 23, aggregated rating 22, storyline 21, franchises 12, collections 20, similar games 25. 25 recently added **obscure** games: summary **14**, cover 23, first release date **15**, genres 21, screenshots 17, artworks 13, age ratings **2**, rating **0**, franchises 1. A game with no summary, date or rating is normal and the mapping must tolerate it. External games are noisy: **206 of the famous sample's external rows are Amazon** (over 25 games), so external ids are grouped by source and capped per source. |
| P8 | 60 images fetched 8 at a time in **1.6 s**, all 200, max 0.35 s each. `t_cover_big_2x` is **528x704** (not 528x748: the real aspect is 3:4), `t_1080p` of a cover is **810x1080** (not upscaled beyond the original), `t_cover_small` 90x120, `t_screenshot_big` 940x529. Cover sizes are 54 to 77 KB at 2x and 108 to 137 KB at 1080p. |
| P9 | Platform table tags resolve: PS4, PS5, PC (to "PC (Microsoft Windows)"), Switch, Xbox One, PS3, 3DS, Wii U, Mac, Linux, iOS, Android; "Xbox Series X" does not resolve (IGDB says "Xbox Series X|S"). Slugs use only `a-z`, `0-9`, `-`. |
| P12b | The one-request OR query works (200 for all six titles; results above in 6.3). |
| P13 | `fields *, <expansions>` returns **every key `*` returns (44 of 44)** plus the expanded objects, including company logos (`involved_companies.company.logo.*`), localizations (`game_localizations.*`: name, region), age ratings with organization, category and content descriptions, and language supports; **200 in 0.25 s, 175 KB on the wire** (114 KB compact) for Fallout: New Vegas, of which 44 KB is its 196 keywords and about 40 KB is nested checksums and timestamps. A 10-result search with every expansion: **565 KB, 0.51 s** (so candidates are fetched lean). A multiquery of three queries (game with some expansions, `game_time_to_beats`, `characters` where `games = (id)`): **200 in 0.16 s, 29 KB, with 1, 1 and 36 results**. The `characters` endpoint returns `name, slug, url, games` (and `description`, gender, species, mug shot when present); `game_localizations` carry only name and region, not covers, for this game. |
| P11 | Latency (median, max): lean search 0.11 s, 0.17 s; full fetch 0.18 s, 0.24 s; time to beat 0.11 s, 0.23 s. Payloads: lean search 2.3 KB, one full record 21 KB, a **full-field 10-result search 80 KB** (so the lean search of 7.1 is right), time to beat 118 bytes. 12 searches at concurrency 2 took 0.76 s and at concurrency 3 0.74 s, all 200. |

**P10** (brand colour contrast) is the only item not run; it is done when the manifest is written. The third probe (`tools/igdb_probe3.py`) closed P12b and P13.

Anything the prototype contradicts changes this document first, as it did for TheAudioDB.

## 18. Decisions for the owner

1. **Type name: `game`, DECIDED 2026-11-05 (first read as `games`, corrected while building).** The owner wrote "game instead of games". The shipped IMDb plugin already declares `game` ("Video Games") and its code says it is shared with the IGDB, LaunchBox, RAWG and Steam plugins as one games type, the Wikipedia plugin and the eight scaffolds say `game`, and the dev database already has that row. `games` would have made a second, empty library. No Wikipedia declaration change and no scaffold edits are needed (12.12, 12.13 and 21).
2. **Interactive search:** IGDB takes part in Add Media and scan search (recommended; one request).
3. **Franchises:** tag only now (recommended), or begin the host work to let games use collections.
4. **Host field names** for developer, publisher, platforms and release date: add them to `FieldMap` and the Assignment page now or after a second games plugin exists (recommended: after).
5. **One item per game** (recommended), or add a `shared_id_scopes` row for `game` (a host settings change) so the same game can exist per platform.
6. **Time to beat as `runtime_minutes`:** off (recommended) or on, accepting that "time spent" then counts the average completion time for every game marked played.
7. **A games scanner** (installer folders, ROM sets) is out of scope here.
8. **Default pace** of 3 requests a second (the documented limit is 4).
9. **Restart: DECIDED 2026-11-05.** Nobody is using Chronicle, so the documented restart after installing is accepted; no sync-after-install host change.

10. **Size of what is kept.** Keeping every field IGDB sends, with nested expansions, costs roughly 30 to 120 KB per game (about a third of that is the nested rows' own `checksum`, `created_at` and `updated_at`). Default per your rule: keep all of it. The only trim on offer is dropping those three bookkeeping fields from nested rows (about 35% smaller, no title data lost); say if you want it.

## 19. Risks

* **Terms.** Free for non-commercial use under the Twitch Developer Service Agreement; the docs differ on commercial use. A personal self-hosted library fits. If Twitch changes the terms or requires user tokens, items stay Pending.
* **A Twitch account with two-factor authentication**, and 25 active tokens per application (10.6).
* **Fuzzy search and clones.** Mitigated by verification, type classification and the year cap, not eliminated. Fix match corrects any match.
* **Same name, different game** (a 1993 and a 2016 *Doom*): the year rule when a year is known, popularity otherwise. **Add Media carries no year**, so there popularity alone orders them.
* **NotFound is terminal.** A game IGDB adds next month stays NotFound until a person resets it.
* **Deprecations** are the likeliest cause of a sudden failure; handled (9.7) with a degraded health state.
* **Crowd-sourced data.** Covers and dates change; the weekly re-sync is the remedy; removed images persist 30 days.
* **Residual unmeasured behaviour** is listed in 2.3 (one-request OR query, steady-state rate, long Twitch outage).
* **Mature content.** IGDB lists adult titles with age ratings; the plugin returns what exists and filters nothing.
* **The first pass** is long but bounded and resumable (9.9); a resync is not resumable.

## 20. What the review changed

An independent reviewer read this design against the host source and the IGDB docs. **Blockers found and fixed:**

* **NotFound is terminal in the host**, so "retried after the retry window" was false, and returning an empty list on a 400 would have turned the whole library permanently NotFound the day IGDB deprecated a field. A failing search now throws the pause form; an empty result is returned only for a genuine empty 200 (9.2, 9.7).
* **The host's behaviour differs on every caller**: the follow-up fetch swallows all exceptions, Add Media swallows everything silently, the add path calls every failure a bad id, Refresh shows the plugin's message to the user, the guard's retries still sleep when the plugin fails fast. The design now has a per-caller table (9.1), a lean-then-full record that survives a swallowed follow-up (7.1), and an interactive path that returns an empty list instead of an exception (9.2).
* **Not-configured was `Skipped`** (terminal) and a rejected secret cost two failing calls per item on the whole queue, because `AuthFailed` does not stop the pass. Missing credentials now throw `PluginAuthException`, and a credentials latch makes every later call free until the settings change (9.3).

**Important corrections:** `GetByIdAsync` not-found semantics and tolerant JSON reading, since a thrown `KeyNotFoundException` or `InvalidOperationException` is a permanent state (7.4); poison items that would stall the queue (9.4); the `ProviderCallGuard` description (9.1, 9.6); the type exists only after a restart (12.1); aliases do not bridge `game` and `games`, and Wikipedia already declares `game` (4, 12.12); the shared-id mechanism is `shared_id_scopes`, not `same_work_media_types` (5.5); the runtime consumers are `StatsService`, `ReportService`, `ScrobbleService`, not `WatchDeriver` (12.9); the request budget is per method and the throughput claim is a floor with a latency probe added (6.6, 9.9, P11); resync-all aborts and restarts (9.1); main-title matches could pass the gate (6.4); Roman-numeral folding collided "Mega Man X" with "Mega Man 10" (6.3); query escaping rested on unknown parser behaviour, so characters are removed instead (6.3); Fix Match validation belongs to the plugin and `GetImageAsync` needs redirects off and parsed-host checks (5.2, 11); the search is now lean (7.1); csproj, `min_chronicle_version` and test corrections (16, 15).

**Owner corrections, 2026-11-05 (after the live probe):** (1) *Get all available data; filter nothing out.* The caps on screenshots, artworks, keywords, alternative names, similar games and external ids, the game-type exclusion list, the vote threshold on ratings, the "characters as a count only" and "company logos not stored" rules, and the default-off time to beat were all removed; executable names are now moved to their own field rather than dropped, and the raw source record is kept (7.2, 8, 13). (2) *Each plugin works standalone and the build order does not matter.* IGDB no longer waits for Wikipedia, and Wikipedia no longer requires IGDB data (it uses it only as optional corroboration); Wikipedia is last in the lists and its data is verified by its own guards (12.13). Both are saved as standing preferences.

**Minor corrections:** the deprecated `category = 1` fallback is gone; artworks are typed `Fanart`; the `sourceUrls` rule and the library-wording alias were dropped as no-ops; the cover is no longer repeated at three sizes; time to beat defaults off; the 25-token limit and the settings-visibility wording were added; the licence pages' disagreement is recorded; scan folders of type `games` are warned against; operational constants are described as technical limits.

## 21. What building it changed (2026-11-05)

Plugin v1.0.0, **255 tests passing, 13 live tests skipped** (they run only with `IGDB_LIVE=1` and credentials), on recorded real responses. Changes from the design above:

* **The type is `game`, not `games`.** Found only when the plugin was deployed to the dev environment: the database already had a `game` ("Video Games") row, created on 2026-10-02 by the shipped IMDb plugin, whose code comment says it is "shared with the game plugins (IGDB, LaunchBox, RAWG, Steam): one games type in Chronicle". The review had checked Wikipedia and the scaffolds but not IMDb. Chronicle merges every plugin's declaration of one type by name (first non-default verb and unit, first display name), so IGDB declares it **identically to IMDb**: display name "Video Games", verb "played", unit **"percent"** (not "hours"; the unit is a label nothing reads), one level, label "Game". Nothing else in Chronicle changes.
* **`plugins.json` tags are `game`, `games` and `metadata`** (IMDb's entry uses `games`).
* **Query text.** The fuzzy search and the exact-name query are built from `TextRules.CleanQueryText`, which turns tabs and line breaks into a space and removes `"`, `\`, `;`, control characters and U+FFFD (a first version deleted line breaks and glued words together).
* **Edition stripping** leaves a title whole when a bracketed qualifier that names a different release ("(Deluxe Remastered Edition)") is kept, instead of trimming the closing word.
* **A version of another game** (a record with a `version_parent`) is penalised even when its type is Main Game.
* **A failure during a cool-down is its own kind** (`cooldown`), so the host's retries of a fail-fast call can never count against an item.
* **The title is read in either order** ("Doom (2016) [PS4]" or "Doom [PS4] (2016)").
* **Search candidates come back lean; the winner is fetched in full inside `SearchAsync`**, exactly as 7.1 says, so the host's follow-up fetch costs nothing.
* **Deployment.** The plugin is in `plugins.json`, `RunTestEnvironment.ps1` and `Deploy-Plugin.ps1`, and `imageExtractor.ts` maps the `Screenshot` image type to the backdrop slot (with a test). The running dev API embeds the plugins.json it was built with, and the catalog on GitHub does not list IGDB until this is pushed, so the **allowlist refuses IGDB in the running dev environment until the API is rebuilt** (the next `RunTestEnvironment.ps1` does that). The admin switch `plugins.allow_unlisted` was **not** used: setting it was blocked as a security relaxation, and the plugin was instead verified in a separate copy of the host built from the current source (below).
* **Not done:** the live tests against IGDB (they need the owner's credentials in the environment of whoever runs them); a GitHub repository and release for the plugin; the Wikipedia guards (12.13); the docs under `docs/plugins`.

### 21.1 Changes from an independent code review of the built plugin

A second independent review (of the code, not the design) found no blocker and these defects, all fixed with tests (now 300 passing):

* **Field drift** could switch every expansion off for the life of the process and could not cope with the characters sub-query. Characters are now their own group; a group comes back after an hour; a record fetched while any group was off is marked `degraded` and `partial` so a re-sync replaces it.
* **The credentials latch** never recovered and could latch corrected credentials from a stale answer. Saving settings now always clears it; it expires after 15 minutes (one probe); a mint or rejection in flight when settings change is judged against the credentials it used, and its token is not kept. The README tells you to reset the Authentication Failed items (the host does not requeue them).
* **Matching:** "Resident Evil 4 Remake" could never match (IGDB records the remake as a type); edition words conflated different games ("Pokemon Gold" and "Platinum", "Persona 5 Royal" scoring below the base game, "Super Smash Bros. Ultimate" matching the 1999 original). The default edition words no longer include words that are often part of a game's name, a bare trailing word counts only before "Edition" or "Version", the edition tier is below an exact title, and removal on both sides is never accepted. DLC words are whole words. Sequel-number variants swap only the last number and never 10.
* **Add Media** found almost nothing a person types (a literal exact query, store typography sent as is). Typography is made plain; the interactive path takes two requests and lists similar titles under the gate; the fuzzy fallback searches the whole title before the main title.
* **Counting a failure against an item** during a multi-hour outage would have exhausted the first queued item; it now counts only when IGDB answered other requests since the record last failed, and never for Twitch failures. A deadline that passes opens a short cool-down so the host's retries do not each wait out another deadline. An unexpected exception becomes a pause instead of reaching the host as a permanent "Skipped". A token is used until 24 hours before it expires (a short-lived one for 90% of its life), is held as one immutable value, and a 401 discards only the token that was rejected. An empty search result is remembered for a minute, not ten. Credentials that cannot be Twitch credentials (spaces, non-ASCII) are an authentication failure before any request.
* **Known and left:** the limiter swaps its semaphore on a settings change while requests are in flight (concurrency can briefly exceed the setting), a caller cancelled mid-wait keeps its paced slot, the `Title: Subtitle` alternate-name forms of 7.2 are not produced, the compact `external_ids` summary omits platform and release format (both are in the complete record), characters beyond 500 are not paged, and `GetImageAsync`'s success path is not covered by a test (the image client is not injectable).
