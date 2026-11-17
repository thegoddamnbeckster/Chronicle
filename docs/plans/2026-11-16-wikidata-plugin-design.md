# Wikidata plugin: design

Checklist: `docs/PLUGIN_NEW_TYPE_CHECKLIST.md` (run record at the end of this document). Written 2026-11-16, before any code.
Repo `Chronicle.Plugin.Wikidata` (a stale scaffold with the old interface and a manifest naming media types that do not exist; it is
replaced, not extended: K6). Plugin id `chronicle.plugin.wikidata`, source `wikidata`.

## 1. Purpose and scope

Wikidata is the structured side of Wikipedia: one item (`Q118645527`) per thing, with typed statements and the ids the same thing has
at other services. The plugin does two jobs and nothing else:

1. **A cross-reference hub.** An item found by any id Chronicle already holds (IMDb, TMDB, TheTVDB, TVmaze, MusicBrainz, Steam, IGDB,
   Open Library, AniList) publishes every other id it knows, in `ExtendedData.ids`, so the host can seed enrichment rows for the other
   plugins without a text search (the host's seeding reads exactly that block, `CrossRefHelper`).
2. **Structured facts**: label and every alias in every language (as `AlternateNames`), release year, genres, the people and companies
   credited (with their photo or logo where Wikidata has one), the item's own image, logo and poster, links to other sites, and the
   complete entity kept raw.

Not in scope: prose (`overview`: Wikidata's one-line description is not an overview, and the Wikipedia plugin supplies real text), ratings,
people items, TV seasons and episodes beyond what an id lookup finds, any write to Wikidata.

**Standalone (SA).** No key, no account, no other plugin. It supplements; it never requires another plugin's data, and nothing requires it.

## 2. What was measured (live, 2026-11-16, 22 requests, paced 1.6 s, descriptive User-Agent)

| # | Question | Result |
|---|---|---|
| W1 | Is there a reverse lookup by another service's id? | Yes: Action API `list=search&srsearch=haswbstatement:P1733=553850` returns the item (0.3 s). Steam 553850 gave `Q118645527` (Helldivers 2); TMDB movie 603 and IMDb `tt0133093` both gave `Q83495` (The Matrix); MusicBrainz artist `a74b1b7f-...` gave `Q44190` (Radiohead). An id Wikidata does not hold gives `totalhits: 0` and an empty list. |
| W2 | Size of one full entity | **213 KB** for Helldivers 2 (159 properties, 24 sitelinks, labels in 8 languages); a three-entity batch with claims was **472 KB**. Each call 0.3 to 0.6 s. |
| W3 | Are labels in English? | **Mostly not.** Most items now carry the label in the language-neutral `mul` code, not `en` (Helldivers 2 and The Matrix have only `mul`; the English-only request returned `mul`). Read order must be `en`, then `mul`, then any language. |
| W4 | What does an error look like? | **HTTP 200 with an `error` object** (`no-such-entity` for a missing or malformed id; `maxlag` when the replication lag exceeds the client's `maxlag`, with a `Retry-After` header). The status line says nothing. A client that checks only the status reports a missing item as success and a lagging service as "no data". |
| W5 | Does `maxlag` matter? | With `maxlag=5` the service answered `maxlag` at once because a query-service host was 36 s behind. So the plugin sends **no** `maxlag` for reads (they are served from the primary store), and treats a `maxlag` error, if ever seen, as a pause. |
| W6 | Text search | `wbsearchentities` (0.3 s) returns ranked items with a one-line description, but is ambiguous by nature: "Dune" returns a landform, a novel series, a 1992 game, a band, an album and a surname. Automatic matching by name is therefore never done (I5); the interactive search filters by class (section 6). |
| W7 | Does Wikidata have Arrowhead's logo? | **No.** The developer item (`Q16833903`) has a website, inception, country, headquarters and instance-of, but **no `P154` (logo) and no icon**. Logos exist only for companies where someone uploaded one, and they are a single image, not a light and a dark version. |
| W8 | Any rate-limit signal? | No `X-RateLimit` headers on reads and no 429 in 22 requests. Wikimedia's policy asks for a descriptive User-Agent with contact, serial requests and respect for `Retry-After`. The plugin keeps requests **at least 1 s apart, one at a time**, shared across everything the plugin does. |

Not measured (and stated, not guessed): a 429 or 5xx from Wikidata; a mass-lookup pattern; the size distribution over the whole library; the
share of the owner's titles that Wikidata holds ids for. The first run in the owner's library reports the last.

## 3. Ids (I1 to I4)

- Own id: `wikidata:Q<digits>`. Accepted wherever an id is typed: `wikidata:Q42`, bare `Q42` (case-insensitive), `https://www.wikidata.org/wiki/Q42`,
  `https://www.wikidata.org/entity/Q42`, `https://m.wikidata.org/wiki/Q42`, `https://wikidata.org/wiki/Special:EntityData/Q42.json`. Hosts
  are compared as parsed `Uri.Host` against an exact list, `https` only. Anything after the number (`Q42x`, `Q42/extra`) is refused, not trimmed.
- **Found by an id Chronicle already holds**, through `KnownExternalIds`: `imdb` (only `tt…`), `tmdb` (`movie:N` or `tv:N`; the kind picks
  `P4947` or `P4983`), `tvdb` (`P4835`), `tvmaze` (`P8600`), `musicbrainz` (`artist:`, `release-group:` or `recording:` picks `P434`, `P436`,
  `P4404`; the hierarchy level must agree), `steam` (`P1733`), `igdb` (`P5794`, slug), `openlibrary` (`P648`), `anilist` (`P8729` anime, `P8731` manga).
  More than one item holding the same id is a Wikidata duplicate: the answer is none, never the first (I5, I6).
- **Published** in `ExtendedData.ids`, only for the same set and only when the item holds exactly one id of that kind: `imdb`, `tmdb` (bare
  number: the host formats it as `movie:N` or `tv:N` from TMDB's own declared policy; omitted when the item holds both a movie and a TV id),
  `tvdb`, `tvmaze`, `musicbrainz` (typed as MusicBrainz writes them), `steam`, `igdb`, `openlibrary`, `anilist`. Every other external id
  (hundreds per item) stays in `raw` and in `ExtendedData.external_ids`, grouped by property and labelled with the property's name.

## 4. Matching (I5, I6)

Automatic matching is **by id or absent**. With no id, the automatic path (`IsRealHierarchyPosition` true) returns an empty list and makes
no request. The interactive path (Add Media and Fix Match, `IsRealHierarchyPosition` false) searches by name: `wbsearchentities` (7 results),
then one `wbgetentities` for their `instance of` and publication date, **discarding every result whose class does not fit the media type**
(film, TV series, video game, book, musical group / person who is a musician, album, single, song, ...), scoring an exact label or alias match
at 90, a year match +5, a prefix at 60, and listing the rest under the host's gate. A pasted id or URL is the answer at 100.

## 5. What is kept (D1 to D9)

Everything the service returns for the item: all labels, descriptions and aliases in every language; every statement with its qualifiers,
rank and references; every sitelink. It is kept raw under `ExtendedData.raw` (**about 200 KB for a well-described item, 20 to 40 KB for a
sparse one; owner decision 1 below**). Fields mapped out of it: title (`en`, `mul`, else the first), year (earliest `P577`, else `P571`), genres
(`P136`), tags (`P31` instance-of and `P279`-free class labels, `P921` main subject), cast and crew (below), `poster_url` (`P3383`, else `P18`),
`logo_url` (`P154`), `backdrop_url` (`P18` for a game only when no poster was found), `AlternateNames` (all aliases and every other-language
label, none dropped), `SourceUrl` (the Wikidata page). Nothing is capped: a long cast is read **page by page**, 50 ids per request, until all are
resolved; a failure on a later request fails the whole fetch (D6, D9), and the manifest declares `call_timeout_seconds` for it (K13).

Cast and crew resolve the referenced items' labels (and, for a company, its logo `P154`; a person's photo `P18` is not fetched in v1, because it needs the person's whole entity) in batches of 50: a film's `P161` cast; a game's
`P178` developer as cast ("Developers", as IGDB does) and `P123` publisher as crew; crew for `P57` director, `P58` screenwriter, `P162` producer,
`P86` composer, `P344` cinematography, `P1040` editor, `P272` production company, `P750` distributor. The person id is `wikidata:Q…` so one
person or company across many titles is one person.

Images: addresses are `https://commons.wikimedia.org/wiki/Special:FilePath/<file>?width=N`, the form that redirects to the upload server,
which sends `Access-Control-Allow-Origin: *` (a thumbnail of a vector logo is a transparent PNG; M4). A file name is URL-encoded and the
address is never guessed: it is built only from a `P18`, `P154` or `P3383` value Wikidata returned (M1). `GetImageAsync` refuses
(`NotSupportedException`; the browser loads images itself, M2).

A failing service is never "nothing there" (D9): a 429, `maxlag`, a 5xx, a timeout or an HTML answer is retried a bounded number of times
and then fails the fetch; only `no-such-entity` and `totalhits: 0` are data.

## 6. The host contract (section 6a of the checklist)

| Row | Design |
|---|---|
| E1, E2 | A pause (429, `maxlag`, 5xx, timeout, transport error, HTML answer, a body that is not the expected shape) is `HttpRequestException` with a null status or 429. An empty list only for `totalhits: 0`; `GetByIdAsync` of an id Wikidata does not hold is `KeyNotFoundException`. A 400-class answer (an `error` object other than `no-such-entity`) is a pause, never an empty result. |
| E3 | `GetByIdAsync`: garbage or a foreign id is `ArgumentException` (cleared by the host); `no-such-entity` is `KeyNotFoundException`. Tolerant JSON readers: no `InvalidOperationException` can come from a missing property. |
| E4 | Each request 10 s; a call deadline of 55 minutes for an id search and a full fetch (declared with `call_timeout_seconds` 3600, K13; the owner accepts a fetch that takes very long, since every credited person is read in full); on its own deadline the plugin throws the pause form, never lets the guard return "no result". The plugin does not retry a failure the guard retries; its own bounded retries (3) are for the service's 429 and `maxlag` signals with `Retry-After`. |
| E5 | Interactive search returns an empty list on any failure (logged) and answers within 2.5 s of the host's deadline: **one request** (`wbsearchentities`) and the class filter from a second, with the first result list returned unfiltered if the second does not finish. On the automatic path `SearchAsync` fetches the item in full itself, so a swallowed follow-up cannot leave it thin; the interactive list is lean (one request, inside 2 s), and choosing a result calls `GetByIdAsync` for the full item. Every message thrown is user-grade and carries nothing secret (there is no secret). |
| E6 | No credential exists, so there is no latch. |
| E7 | A per-id failure counter: the third consecutive failure for one id throws `InvalidDataException` (Exhausted), so one entity that always errors cannot block the queue. Reset on success. |
| E8 | The entity request names no field (`props=labels|descriptions|aliases|claims|sitelinks|info`); a retired prop would be a `maxlag`-style `error` and a pause. |
| E9 | An HTML answer from `wikidata.org` (a block or maintenance page) is detected from the content type and first byte, treated as a block, held for **every** caller for 30 minutes through the shared pacing, and never bypassed. |
| E10 | Pace measured above: 1 s floor (W8), serial. |
| E11 | Independent review of this design and of the built code: Review A and Review B in section 9. |
| E12 | `plugins.json` entry pushed (the owner approved pushes earlier in this project, but each push is asked again), then restart. |

## 7. Types, settings, tasks, branding

- **Types (T1, T3):** `movies`, `tv`, `music`, `book`, `game`, each declared as the Wikipedia plugin does: name and `DefaultPriority = 95`, no display
  name (it never owns a type). Fields: `title`, `year`, `poster_url`, `logo_url`, `genres`, `tags`, `cast`, `crew` (`extended_data` is not a host field name).
  Priority 95 puts it after Wikipedia (90); Wikidata fills gaps and never wins by default.
- **Settings:** one Notice ("No account or key is needed"), `contact_info` (optional text: appended in the User-Agent in brackets only if the
  owner enters it; the owner's own choice, never filled by the plugin), `min_request_interval_ms` (advanced, default 1200, never below 1000).
  Secrets: none (X3 N/A).
- **Tasks:** `fetch-missing-metadata` on at `22,52 * * * *` (off the hour and half-hour of the other plugins, paced), `resync-all-metadata` off.
  No import half (H1 to H7 N/A).
- **Branding:** icon `https://www.wikidata.org/static/favicon/wikidata.ico` (verified before release, B1); brand colours from Wikidata's blue bar:
  `#006699` on light (6.25:1 on white, 5.67:1 on a pale grey) and `#5fb3dd` on dark (6.0 to 6.9:1 on Chronicle's dark teals), computed (B2).

## 8. Terms (S5)

Wikidata's structured data is released under **CC0**: no attribution or licence condition on the data. Commons images carry their own licences;
the plugin offers their addresses and states in its doc page that an image's licence is its file page's. API use requires a descriptive
User-Agent and polite pacing (done). No account or ban risk beyond blocking an abusive client.

## 9. Reviews and decisions

Owner decisions (defaults chosen, change on request): **1. size**: keep the full entity raw (about 200 KB for well-described items), the checklist's rule
D2; the alternative is dropping references and bookkeeping, about 40 percent smaller. **2. `overview`** not filled from Wikidata's one-line
description. **3. Logos**: used where Wikidata has them (a minority of companies; Arrowhead has none); a company without one shows its initial.

**Review A (design)** was my own reading of this design against the host source before any code (the checklist section 6a was itself verified
against the host in the same session). **Review B (code)** was my own adversarial read of the built plugin. **Neither was independent**: E11
asks for a second reader, and none was run. What the self-review found is in section 10.

## 10. What building it changed (2026-11-16)

Found by the tests and one live run, each fixed with a test that fails on the old code:

- **A quoted statement value matches nothing.** The design and the first build wrote `haswbstatement:P1733="553850"`; the unit tests asserted the quotes
  and passed. The first live run found nothing. Unquoted works (`P1733=553850`, `P5794=helldivers-2`). The value is now held to plain id characters
  instead, which also keeps search syntax out of it (`ArgumentException` before any request).
- **A bare id in an item's name on the automatic path.** `Q42` can be a real title; only `wikidata:` or an address counts there.
- **The interactive class filter** let people and organisations through ("American film director", "ethnic group in Nigeria"); now rejected.
- **No by-name search below the top level** except music (a season or episode name finds nothing on Wikidata).
- **`extended_data` is not a host field name** (the Wikipedia plugin declares it; it is invisible). Not declared here.
- **A general image is not a poster.** Added The Matrix by id in an isolated host: its Wikidata `P18` ("image") is a screenshot of the
  green-code screensaver, and the first build promoted it to `poster_url`. Only `P3383` (film poster) is a poster now; `P18` stays an
  additional image the owner can pin.
- **A typeless search** (the plugin's own search box) listed nothing because no kind was named; it now accepts any kind the plugin serves.
- **A recursion** in the batch fallback (a failed batch of several retried the same batch) found while writing, before any run.

## 10a. v1.1.0: people, games, and an id type the host drops (2026-11-16)

The owner asked about people ("I thought you said that wikidata was going to have people information?") and games. The first build had
left people out of scope without saying so as a departure from the pitch; v1.1.0 adds them.

- **People.** The `people` type, declared as TMDB declares it. A person is found by IMDb name id (`P345`, `nm…`) or TMDB person id (`P4985`),
  written the way the host stores it (`tmdb:521`, `521`, `person:521`; a `movie:` or `tv:` id is never read as a person's). **Never by name**,
  automatically or interactively: TMDB's own design rule for people is "ID-based resolution only", and two people share a name. Fields: title, the
  preferred-rank `P18` portrait as the photo (for a person it is the portrait; for a work it is not promoted), occupations as tags, and
  `birthDate` and `deathDate` in `ExtendedData` under the names the host reads (`FieldMap`), **only when Wikidata knows the day** (precision 11).
  Recorded Michael J. Fox (`Q395274`): holds both `nm0000150` and TMDB person `521`, born 1961-06-09, two portraits, 177 KB, found in 3.6 s.
  This is the link the Back to the Future duplicate was missing; it does not merge anything by itself (see the host change in the Chronicle repo).
- **A person passes on only `imdb` (`nm`) and `tmdb`.** The same item holds a MusicBrainz artist id (he is a musician); handing that on would seed
  a music artist from a person.
- **Games.** Platforms (`P400`), game modes (`P404`) and engine (`P408`) as tags; designers (`P287`), programmers (`P943`), creators (`P170`), art
  directors (`P3174`) and production designers (`P2554`) as crew. A game's series is not turned into a collection (T4, and Wikidata's series data is uneven).
- **The ids were the wrong JSON type.** The host files a published id through the TARGET plugin's policy: `CrossRefValueKinds` is `["number"]`
  for TMDB, Simkl and Trakt and `["string"]` for IMDb, and an id of the other kind is dropped silently (`ExternalIdPolicies.FormatCrossRef`
  returns null). v1.0.0 wrote every id as a string, so its TMDB id would never have seeded anything; the isolated-host run stored the ids and looked
  right, which is why it was not seen. Numeric ids (`tmdb`, `tvdb`, `tvmaze`, `steam`, `anilist`) are now JSON numbers and the rest strings.

### 10b. "We get all information; we don't leave anything out, people included" (owner, 2026-11-16)

The owner's rule, added to the checklist as D10 and rule 6. v1.1.0 still met it only for people **found by id**: credited people were resolved to
a bare name, without their portrait, to save one request each. Corrected in the same release:

- **Every credited person and company is resolved to their own record** (`labels|claims`), not a name: cast, crew, voice actors, band members,
  developers, publishers. The credit carries the portrait (`P18`, preferred rank) or logo (`P154`) when Wikidata has one, and the
  `wikidata:Q…` person id, which makes the host look the person up in turn (their dates, ids, every portrait). Only genres, classes and
  characters are fetched as names alone, since a name is all they have.
- **More of the credit list:** voice actors (`P725`, with the character voiced), a band's members (`P527`, read only on an item that holds a
  MusicBrainz artist id and only for values that are human: for most things `P527` lists episodes or films), executive producers, costume
  designers, lyricists, conductors and choreographers.
- **Measured (live):** The Matrix: 19 cast (12 with a portrait) and 20 crew (15 with a picture) in 5.3 s; Radiohead: its 5 members as cast, all with
  portraits, 4.4 s. A cast of a hundred costs a request per fifty people and a few megabytes each (a person's record is 100 to 180 KB), so minutes,
  which is what the long call deadline (`call_timeout_seconds` 3600) and the background task are for.

### 10c. "The queue keeps running until pending is 0, for every plugin" (owner, 2026-11-16)

The owner accepts that Wikidata may take days, and requires that Chronicle keep going until nothing is pending, moving past any problem item, for
every plugin. That is a host rule, so it was implemented in the host's shared queue (checklist E13), not in this plugin: before it, the first rate
limit or lost connection ended a plugin's whole run, as did an unexpected failed save, and Re-sync All stopped at the first pause. Now a provider that
stops answering is waited for and the queue carries on. Refined the same day (owner): a wait that is longer than a few minutes and is for ONE item does not
hold the queue: the item is spun to the back and the rest is done first, coming back to it at the end (three times at most per run, then it is left for the
next run). Three different items in a row with no answer mean the connection, and then the queue waits and probes, one request per wait. A provider's own
"try again after" (the plugin puts it in `ex.Data["RetryAfter"]`; this plugin does, for a 429, a maxlag, its 30-minute block-page hold and each call made
while held) is honoured: never waited less, and an item held for a long time is not tried again before its hold ends. A run ends early only after twelve hours of silence.
For this plugin the call deadline was raised to 55 minutes (`call_timeout_seconds` 3600) so a very large record is not cut off and then skipped
every run.

## 11. Run record (G1 to G3)

Status key: **Built** (code and test named), **Live** (seen against the real service), **App** (seen in the running Chronicle), **Owner** (needs the owner), **N/A** (reason).
Tests: 186 passing (`tests/`), one live test gated by `WIKIDATA_LIVE=1`. **Isolated host** (a copy of the API on port 7985, an empty database, only this plugin in `plugins/`, the hosted-catalogue address unreachable so the documented embedded fallback applied; no setting was changed): the plugin loaded (`Plugin loaded: Wikidata v1.0.0 - 1 metadata`), its health check was healthy against the live service, its settings schema loaded, Add Media's search for "The Matrix" (movies) returned `wikidata:Q83495` (1999), and adding that id stored the item with the whole record under `chronicle.plugin.wikidata` (ids `imdb` and `tmdb`, 19 cast with characters, 9 genres) and `media_external_ids` `wikidata:Q83495`.

| ID | Status | Where / evidence |
|---|---|---|
| S1 to S3 | Built, Live | section 2; fixtures `tests/fixtures/p*.json`; 22 paced requests plus 3 live lookups |
| S4 | Built | everything kept: `raw` (entity), `external_ids` (all 119 for Helldivers 2), `sitelinks`, names; `MapperTests.The_whole_entity_and_every_external_id_are_kept` |
| S5 | Built | section 8 |
| D1 to D3 | Built | nothing capped; `MapperTests`; the one choice (full raw, 127 to 247 KB) is owner decision 1 |
| D10 | Built, Live | every credited person and company resolved to their own record, portrait on the credit; voice actors, band members, the rest of the credit list; `PeopleAndGamesTests` (D10 section); live: The Matrix, Radiohead |
| D4 | N/A | no deletion path |
| D5 | N/A | no account |
| D6 | Built | batches of 50 until all resolved (`ClientTests.More_than_fifty_ids...`); a failure fails the fetch |
| D7 | N/A | one item per lookup; no browse list |
| D8 | Built | every referenced id in the mapped fields is resolved to its name (companies with their logo claim); unmapped references stay in `raw` |
| D9 | Built | `ClientTests` (5xx, 429, maxlag, HTML, malformed answers all throw; only `no-such-entity` and `totalhits 0` are data) |
| I1, I2, I3 | Built | `IdsTests` (13 accepted forms, 22 refusals) |
| I4 | Built | `ExtendedData.ids` for nine services; withheld when ambiguous (`MapperTests`) |
| I5, I6 | Built | automatic by id only; duplicates match neither (`ProviderTests`) |
| I7 | N/A | host's Clear Match covers it |
| M1 | Built | image addresses built only from returned file names (`MapperTests`); not HEAD-checked: Commons redirects any name, a missing file 404s in the browser (stated limit) |
| M2 | Built | `GetImageAsync` refuses; Commons sends CORS `*` (not re-measured this session) |
| M3 | Built | read in `imageExtractor.ts`: `Poster` and `Logo` map to their slots; `Image` and `Icon` are unlisted and fall into the default poster slot, where they still show and can be re-pinned. Not seen on screen |
| M4 | Built | thumbnails of vector logos are PNG; the card's panel logic is not this plugin's |
| M5 | Built | poster and logo filled when the item has them; Helldivers 2 has neither (measured) |
| C1 to C7 | N/A | no secret, no credential; contact detail is the owner's text, validated (X4) |
| H1 to H7 | N/A | no import half; pacing state is shared in a static hub regardless (H7) |
| T1 | Built | no display name; the type's owner keeps its declaration (`ContractTests`) |
| T2 | App (isolated host) | host started and the plugin loaded after a restart; types are not created by this plugin |
| T3 | Built | priority 95; never owns a type |
| T4 | Open | a collection-level container (a game series, a film collection) matched by id only; **not exercised** |
| P1 to P15 | Open | the app checks are not done: the dev app's cached catalogue does not list the plugin until `plugins.json` is pushed (E12) |
| SA | App (isolated host) | no other plugin installed in the isolated host; load, health, search, add and storage all worked |
| K1 | Built | `ContractTests.Version_comes_from_the_assembly_and_equals_the_manifest` |
| K2 | Built | `min_chronicle_version` 0.20.66 (first build reading `call_timeout_seconds`); not loaded on an older host |
| K3 | Built | `plugins.json` entry added **locally, not pushed**; `RunTestEnvironment.ps1` entry added; tag not made |
| K4 | Built | 186 tests: contract, mapper on a recorded response, settings schema |
| K5 | Built | CI workflow; MIT licence; terms in the doc page |
| K6 | Built | scaffold replaced with the current interface |
| K7 | N/A | no secret exists to redact |
| K8 | Built | `ClientTests.Every_request_names_chronicle...` |
| K9 | Built | one client per configuration; 30 s request timeout under the call deadline |
| K10 | Built | 1.2 s floor (1 s minimum); 429 and maxlag retried 3 times honouring `Retry-After` |
| K11 | Built | no empty catch |
| K12 | Built | `docs/plugins/PLUGIN_WIKIDATA.md`, catalogue line |
| K13 | Built | `call_timeout_seconds` 3600; fetch deadline 3300 s |
| K14 | Open | `PLUGIN_COMPLIANCE.md` not regenerated |
| E1 to E4 | Built | `ProviderTests`, `ClientTests` |
| E5 | Built | interactive never throws; follow-up served from the held item |
| E6 | N/A | no credential |
| E7 | Built | `ProviderTests` (three tries, reset on success, busy service never counts) |
| E8 | N/A | no field list is sent; the request names its props only |
| E9 | Built | `ClientTests.An_html_answer_is_a_block...` |
| E10 | Built, Live | section 2 |
| E13 | Built (host) | the shared queue waits out a silent provider and moves past a problem item; 5 new tests in `MetadataEnrichmentServiceTests` (wait and resume, poison item moved past, 12-hour allowance, resync); not run on the real library yet |
| E11 | Not done | self-review only, not independent |
| E12 | Owner | push `plugins.json` to GitHub, then restart |
| Q1 to Q13 | Partly | Q1 to Q3, Q6, Q7 and Q12 in the isolated host through the API (one typed search, "The Matrix"); the ten typed searches Q13 asks for are not done |
| B1, B2 | Built, Live | icon answers 200; contrast computed (section 7) |
| B3 to B6 | Open | not looked at in the app |
| X1 to X10 | Built | `ContractTests` schema tests; X6 (save, restart, save) not done in the app |
| PK, VP | Open | no release zip, no tag, no verification pass |

Open items, stated plainly: the in-app verification pass (P, Q, B3 to B6, X6, VP) is not done; the plugin is not pushed, tagged or released;
E11 had no independent reviewer; T4 was not checked; `PLUGIN_COMPLIANCE.md` is not regenerated.
