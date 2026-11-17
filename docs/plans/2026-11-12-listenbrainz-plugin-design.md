# ListenBrainz plugin: design and run record

Date: 2026-11-12. Repo: `W:\Scripts\Chronicle.Plugin.ListenBrainz` (`thegoddamnbeckster/Chronicle.Plugin.ListenBrainz`).
Checklist: `docs/PLUGIN_NEW_TYPE_CHECKLIST.md`. Replaces the 2026-03 scaffold (old interface, invalid manifest, a design written
from documentation) and the older version of `docs/plugins/PLUGIN_LISTENBRAINZ.md`.

**State at the end of this session:** built, 152 tests green, checked live against the public API. **Not committed, not released,
not deployed, not looked at in the running app.** G1 (design) and the static half of G2 are done; G3 (verification in the app) is not.

## 1. What "standalone" means here

The owner asked that the plugin "run standalone". Read as the checklist reads it (section 8, "the plugin stands alone"): it needs no
other plugin, no key, and no account to do its work, and it has a test that proves it (`It_works_with_nothing_configured_and_nothing_else_installed`,
`Search_with_no_musicbrainz_id_returns_nothing_and_makes_no_call`). It supplements MusicBrainz and Last.fm when present and never requires them.
If a separate executable was meant, that is a different design: say so.

## 2. What was measured (S1, S3), 2026-11-12, live API, no token

| Endpoint | Result |
|---|---|
| `GET /user/{u}/listens?count=1000` | 200, **public, no token**; returns the full 1000; `max_ts` exclusive; newest first |
| `GET /user/{unknown}/listens` | 404 `{"error":"Cannot find user: ..."}` |
| `GET /metadata/artist/?artist_mbids=&inc=artist tag` | 200, public; array; area, begin_year, type, rels (all links), `tag.artist[]` with `count` and `genre_mbid` |
| `GET /metadata/recording/?recording_mbids=&inc=artist tag release` | 200, public; object keyed by mbid; recording (length, isrcs, rels, url_rels, first_release_date), release (caa ids), tags per entity |
| `GET /metadata/release_group/?release_group_mbids=&inc=artist tag` | 200, public; release_group (date, type, rels, caa ids), tags (73 release-group tags on one record) |
| `POST /popularity/{artist,recording,release-group}` | 200, public; `total_listen_count`, `total_user_count` |
| `GET /stats/sitewide/artists` | 200, public |
| `GET /metadata/lookup/` (name lookup), `GET /popularity/top-recordings-for-artist/` | **401 without a token** |
| `GET /popularity/top-release-groups-for-artist/` | 200 but **1.3 MB** for one artist; not used |
| `inc=rels` | 400: valid `inc` values are artist, tag, release, recording, release_group |
| Rate limit | documented **1 call per second per client**; live window **30**, headers `X-RateLimit-Remaining/Limit/Reset/Reset-In` (the old doc's "50 per second" was wrong) |
| Cover Art Archive `release/{caa_release_mbid}/{caa_id}-500.jpg` | 200 image/jpeg after redirects, CORS open |
| `https://listenbrainz.org/static/img/favicon.ico` | 200 image/x-icon |

Not measured (needs the owner's token, or a private account): `/validate-token`'s real answer, `/metadata/lookup`'s shape, what a **private**
profile's listens endpoint answers. See section 6.

Fixtures are in `tests/fixtures` (account name replaced by `testuser`; the rest is public MusicBrainz and listen data).

## 3. Design

- **Import** (`IImportProvider`): `GET /user/{name}/listens`, 1000 per page, newest first with `max_ts`, until a page comes back short;
  an incremental run stops at one day before the last sync. Page boundaries step one second past the oldest so listens sharing a
  second are not lost (the seen-set removes repeats); a page that is all one second ends rather than loops. One listen is one
  `ImportedWatchEvent`, id `listenbrainz:recording:<mbid>`, `AdditionalIds.musicbrainz = recording:<mbid>`, or
  `listenbrainz:msid:<uuid>` (ListenBrainz's own id for the listen) when no MusicBrainz id exists. A failing page fails the whole import.
- **Metadata** (`IMetadataProvider`, music, 3 levels): by MusicBrainz id only. Artist ← `/metadata/artist` + `/popularity/artist`;
  album (release group) ← `/metadata/release_group` + `/popularity/release-group`; track (recording) ← `/metadata/recording` + `/popularity/recording`.
  Declared fields equal filled fields (a test holds them equal): artist title/genres/tags; album + year, poster_url; track + runtime_minutes.
  `DefaultPriority` 25 (after MusicBrainz 10 and Last.fm 20).
- **Why no name search:** `/metadata/lookup` is token-gated and unmeasured; guessing from a name breaks "automatic matching is exact or absent" (I5).
- **Ids (owner rule, 2026-11-12): one kind and one uuid per id, always `listenbrainz:`-prefixed.** `artist`, `releasegroup`, `recording`,
  `track` (a MusicBrainz track, distinct from a recording) and `msid`. An earlier draft glued a release-group onto a recording id
  (`recording:<mbid>@<release-group>`) and built a name id from artist/album/title; both mixed things inside one id and are gone. The host
  does not need the album in the id: it matches a music play by the `musicbrainz` recording id in `AdditionalIds` before it looks at
  names (`SyncOrchestrationService.MatchOrCreateMusicAsync`), and never stores a play's own id on an existing item.
- **Tags:** a tag with `genre_mbid` is a genre, the rest are tags; all kept, most-voted first.

## 4. Findings worth knowing

1. The old design doc was wrong about auth (token not needed for the main data) and the rate limit.
2. `ImportedWatchEvent` has **no field for the rest of a listen** (`additional_info`: submission client, duration, track number, artist ids,
   ISRC, tags, and `mbid_mapping.url_rels`). D2 cannot be met for the import half through this contract. A host change (a raw/extra field
   on the event) would let it be.
3. `release_name`-less listens exist; those events import with no album (the host files them as it does for Last.fm).

4. **ListenBrainz has bot protection, and my own testing tripped it (2026-11-12).** After a run of full 1000-listen history pages (about 40 in
   a row, one every 1 to 2 seconds, on top of an earlier 19,371-listen import), the service began answering with **HTTP 200 and an HTML
   "Verifying your browser" page** instead of JSON. The plugin reported it as "non-JSON answer" and failed the import, which was the right
   outcome but the wrong message and the wrong follow-up. Chronicle does not and must not try to pass such a check. Now: the page is
   recognised (HTML content type, or a body starting with `<`), the call fails with a clear message and status 429, **every caller in the
   process is held for an hour**, and later calls are refused at once instead of slept through. History pages are also read **10 seconds
   apart** (`ImportProvider.DefaultPageGap`) rather than a second apart: a bulk download is exactly what such protection is for, and speed does
   not matter. All live probing was stopped when the page appeared.

## 5. Run record (checklist section 12)

Status words: **Built** (code and the test that covers it), **Designed**, **Owner action**, **N/A** (with reason), **Gap** (known, not done),
**Not done** (verification in the app, not yet run).

| ID | Status | Where / evidence |
|---|---|---|
| S1 | Built | fixtures listed in section 2; token-gated endpoints not recorded (Owner action, section 6) |
| S2 | N/A | official documented API; the unmeasured token endpoints are section 6 |
| S3 | Built | rate limit, page size, response sizes measured (section 2). Private-profile behaviour **not measured** (Gap) |
| S4 | Built / Gap | metadata: every field kept in extended data + `raw`. Import: see section 4 item 2 (Gap, host contract) |
| S5 | Built | read 2026-11-12 (WebFetch of the API docs and metabrainz.org/api): ListenBrainz documents **one call per second per client** and requires a User-Agent with name, version and a contact URL or address; the pages I could reach state **no data licence or commercial-use terms**, so those remain unconfirmed (owner action before commercial use) |
| D1 | Built | no cap, trim or filter; grep of `Take(`, `limit`, `Substring`, `Math.Min` found only wait-time maths and the page size; tests `A_history_larger_than_one_page_*`, 2500 listens read |
| D2 | Built (metadata) / Gap (import) | `raw` on every metadata record; import: section 4 item 2 |
| D3 | N/A | music tracks only |
| D4 | Built | the plugin never deletes; an unknown user is an error, not an empty list (`An_unknown_user_is_an_error_*`) |
| D5 | Gap | account-level facts (`/user/{u}/listen-count`, stats) are not imported; no account record exists for music yet |
| D6 | Built | paged until a short page; a later page failing fails the import (`A_page_that_fails_fails_the_whole_import_*`) |
| D7 | Built | the metadata answers are not partial lookups: an album returned 73 release-group tags, an artist 27 tags; no browse step needed |
| D8 | Built | `genre_mbid` expanded into genres vs tags; artist credits and relationship lists kept whole |
| D9 | Built | 5xx / 429 / dropped connection retried a bounded number of times then thrown, never `null`; a browser-check page is a block, not data (`A_failing_service_throws_*`, `A_browser_check_page_is_a_block_*`) |
| I1 | Built | `listenbrainz:` on every id in both halves, one kind and one uuid each (`Every_issued_id_is_the_source_prefix_one_kind_and_one_uuid`, `No_issued_id_holds_more_than_one_uuid_*`, `Every_event_id_is_one_kind_and_one_value`) |
| I2 | Built | prefixed, bare, `release-group:` / `release_group:` / `releasegroup:`, listenbrainz.org and musicbrainz.org URLs; anything after the uuid is refused (`ListenBrainzIdsTests`) |
| I3 | Built | ids are lowercased and compared bare |
| I4 | Built | `AdditionalIds.musicbrainz`, `external_ids` in extended data; nothing requires another plugin |
| I5 | Built | search by id only; no id means no call (`Search_with_no_musicbrainz_id_returns_nothing_and_makes_no_call`) |
| I6 | N/A | nothing is merged by name |
| I7 | N/A | the host's `DELETE /media/{id}/external-ids/<plugin id>` applies unchanged |
| M1 | Built | the one image kind (cover) is built from the record's own ids and was checked live (200 image/jpeg) |
| M2 | Built | https, hot-linkable, CORS open; `GetImageAsync` refuses with a reason (`GetImageAsync_is_a_refusal_with_a_reason`) |
| M3 | Built | only `poster_url` is produced |
| M4 | N/A | no logos or wide images |
| M5 | Built | `poster_url` filled whenever ListenBrainz has a cover id; the other slots have no source |
| C1 | Built | token is `Password`; sent only as a header |
| C2 | Built, **Owner action to read in the UI** | text in `GetSettingsSchema`; to be read as a stranger would at G2/G3 |
| C3 | Built | no assumption about token or username format (names may hold spaces) |
| C4 | Built | rejected token: one call then latch, cleared by Configure (`A_rejected_token_costs_one_call_then_latches`) |
| C5 | Built | 401 with a token (latched) vs 401 without (a token is needed for this call, not latched) |
| C6 | N/A | no refreshing token |
| C7 | Built / Gap | named: unknown user, rejected token, needs a token, **ListenBrainz browser check**. A private profile's answer is still unmeasured |
| H1 | N/A | library import is for owned titles; this is a play history |
| H2 | Built | stable ids; the host dedupes by exact timestamp; the one-day overlap is safe |
| H3 | Built | a failing page throws; a rate limit is `HttpRequestException` carrying 429 |
| H4–H6 | N/A | library/tombstone/series concepts |
| H7 | Built | one static pacing state shared by both registry instances (`The_metadata_and_import_clients_*`) |
| T1 | Built | `MediaTypeName`, `DisplayName`, `HierarchyLevels`, `HierarchyLabels`, `InteractionVerb` compared by reading against MusicBrainz's and Last.fm's music declarations: identical; `ContractTests.The_music_type_is_declared_like_the_other_music_plugins` holds them |
| T2 | Owner action | restart the host after install |
| T3 | Built | `EnrichOnly` false (an id search is fast), `DefaultPriority` 25, no collections for music |
| T4 | N/A | music has no collection level |
| P1–P15 | **Not done** | nothing was looked at in the running app |
| SA | Built | tests named in section 1 |
| K1 | Built | version read from the assembly; both halves equal the manifest (`Both_halves_and_the_manifest_report_one_version`) |
| K2 | Built | the plugin needs a host built on or after 2026-10-05: `ImportedWatchEvent.ArtistName` (commit eae1d47) and `ScoredCandidate.IdentifierMatch` (718aee3) arrived that day; Last.fm needs the same. Loaded into the current host (v0.20.71 line) in this session. An older build was not run |
| K3 | Built (local) / Owner action | `plugins.json` entry added; `audit-plugins.py` now reports every check passing for ListenBrainz **except** K3's tag (needs a commit and `v1.0.0`, which wait for your go-ahead). No embedded copy exists for catalogue-only plugins other than the ones `RunTestEnvironment.ps1` builds, and this one is built there |
| K4 | Built | 124 tests: contract, mappers on recorded responses, settings schema, redaction |
| K5 | Built / Owner action | CI workflow and LICENSE copied from Last.fm; source terms: S5 |
| K6 | Built | scaffold replaced with the current template |
| K7 | Built | `SecretProofTests`: the token never appears in an exception, even when the service echoes it |
| K8 | Built | `Chronicle-ListenBrainz-plugin/<version> (+https://github.com/thegoddamnbeckster/Chronicle.Plugin.ListenBrainz)`: name, version and contact URL as ListenBrainz requires; no personal detail (`The_user_agent_names_the_plugin_*`) |
| K9 | Built | one client per configuration, 60 s timeout (the import is a background task; interactive calls take the host's token) |
| K10 | Built | one call per second at least (1.2 s floor), the window headers waited out at 2 calls left, a 429 or a browser check holds every caller, history pages 10 s apart (`A_429_holds_every_other_caller_*`, `After_a_browser_check_every_call_is_refused_at_once_*`, `A_gap_is_left_between_pages_but_not_after_the_last`) |
| K11 | Built | no empty catch; the one retry catch states its reason; others log with the plugin name |
| K12 | Built | this page, `plugins/PLUGIN_LISTENBRAINZ.md`, the catalogue line |
| K13 | Built | the import is not run under the host's 25 s call guard (`ImportService` / `SyncOrchestrationService` call `GetWatchHistoryAsync` directly; only enrichment, search and health go through `ProviderCallGuard`). A metadata fetch is two calls, about 1 to 2 s at the documented pace, well inside 25 s. No `call_timeout_seconds` is needed |
| K14 | Built | `survey-plugins.py` + `audit-plugins.py` run to the scratchpad (not the repo file): ListenBrainz passes all 21 static checks except K3 (tag, see above). `docs/plugins/PLUGIN_COMPLIANCE.md` itself was **not** regenerated, to avoid committing a matrix for all 91 repos with this change |
| Q1–Q3, Q6, Q7 | Built | tests in `ListenBrainzProviderTests` |
| Q4, Q5, Q8, Q9, Q11 | N/A | no name search, no aliases, no multi-platform games |
| Q10, Q12, Q13 | **Not done** | no typed queries in the app |
| B1 | Built | `favicon.ico` answers 200 (checked live); legibility on light and dark not looked at |
| B2 | Built | `#353070` on white 11.6:1; `#8e88e0` on `#0d0d0d` 6.3:1, on `#242424` 5.0:1 (computed) |
| B3 | **Not done** | light-theme look at the Background Tasks card |
| B4, B5 | Built | manifest text states what it imports, what it needs, that it works alone, and every Fix Match form |
| B6 | Built | all four tasks named and described; the full import has a confirmation |
| X1, X3, X7 | Built | schema and manifest agree (manifest has no `settings` list on purpose); `ContractTests` |
| X2 | Built | help text names where, what, an example, what not (`The_help_text_says_where_what_and_what_not_to_enter`) |
| X4 | Built | bad username or token falls back to unset with a reason (`ContractTests`) |
| X5, X9 | N/A | no advanced or behaviour-changing settings |
| X6 | **Not done** | save / restart / save round trip in the app |
| X8 | Built / Gap | Test = one safe read plus a token check; reports true/false, the words go to the log (the host shows only HEALTHY/UNHEALTHY) |
| X10 | Built | `ContractTests` |
| PK | **Not done** | `plugins.json` entry, embedded copy, `Deploy-Plugin.ps1`, release zip. `RunTestEnvironment.ps1` build entry **was added** |
| VP | **Not done** | G3 |

## 6. Open items for the owner, in order

1. Commit the two repos (nothing is committed). Say "commit" and I will.
2. Run `.\scripts\RunTestEnvironment.ps1`; it now builds and deploys the plugin. Approve the changed files on the Plugins page; restart the host (T2).
3. Plugins → ListenBrainz: enter your username; leave the token empty. Run **Import Full Listen History** and watch Background Tasks (K13).
4. Settings → Metadata Assignment: add ListenBrainz to the Music lists (P2, P3).
5. If you want name lookup: put a token in, and ask for the probe of `/metadata/lookup`, `/validate-token` and a private profile. That is the one thing that could
   not be measured without a credential, and it decides whether a name search is worth building.
6. Release (K3, PK): zip, SHA-256, `plugins.json`, tag `v1.0.0`.

## 6a. Standalone, proved (no MusicBrainz plugin)

1. By construction: the plugin project references only `Chronicle.Plugins`, `Chronicle.Core` and Serilog (`Chronicle.Plugin.ListenBrainz.csproj`).
   No type of the MusicBrainz plugin is referenced, and the manifest declares no dependency.
2. By test: `It_works_with_nothing_configured_and_nothing_else_installed`, `Search_with_no_musicbrainz_id_returns_nothing_and_makes_no_call`.
3. By running it in the real host with nothing else installed: a throwaway copy of Chronicle (port 7990, empty database, `plugins/` holding only
   `chronicle.plugin.listenbrainz`). The host log says "Found 1 enabled plugin(s) ... Plugin loaded: ListenBrainz v1.0.0 - 1 metadata, 1 import". Through
   the host's own API, with ListenBrainz the only plugin: health `healthy: true`; search of a pasted MusicBrainz URL returned
   `listenbrainz:recording:8f3471b5-...` "Never Gonna Give You Up" at 100; search of `listenbrainz:artist:a74b1b7f-...` returned Radiohead at 100;
   `GET /metadata` returned OK Computer (1997, cover art URL, 14 genres, 59 tags) and the recording (1987, 9 genres, 21 tags). The first
   full import attempt through that host reached ListenBrainz and failed only on the bot check described in section 4, not on anything missing.
4. A music item reaches the metadata half by carrying a MusicBrainz id **as data** (an imported listen brings it in `AdditionalIds`; a tagged file
   brings its own). Nothing needs the MusicBrainz *plugin* to be present to hold that id.

## 7. Code review (2026-11-12), findings fixed

1. **Cross-reference prefixes: corrected twice.** The first build declared bare prefixes (`artist:` and so on); I removed them on the claim that
   the host would seed this plugin with Last.fm's `artist:Radiohead`. That claim was wrong: the host seeds from other plugins' `ExtendedData.ids`
   formatted as `source:value`, and neither Last.fm nor MusicBrainz publishes such ids, so nothing was at risk. The owner asked for the ids to
   stay, namespaced. They are back as `listenbrainz:artist:`, `listenbrainz:releasegroup:`, `listenbrainz:recording:`, `listenbrainz:track:`
   (test `Accepted_cross_reference_ids_are_only_ever_in_this_plugins_own_namespace`).
2. **Pacing was 120 ms, the documented rule is one call per second.** Now one per second, shared by both registry instances.
3. **A 429 slept on its own caller only.** Other callers kept firing into the limit. The wait now goes through the shared pacing state (`A_429_holds_every_other_caller_until_the_wait_is_over`; the first draft of that test passed on the old code because it timed the wrong thing, and was rewritten until it failed there).
4. **User-Agent had no plugin version.** ListenBrainz asks for name, version and contact. Fixed.
5. **A username of `.` or `..`** would be folded into the request path and ask for another endpoint. Refused with a reason.
6. **Bot protection** (section 4 item 4): recognised, never bypassed, held for an hour for every caller, history read 10 s apart.
7. **Ids are one kind and one uuid each** (owner rule): `artist`, `releasegroup`, `recording`, `track`, `msid`; the glued `recording@release-group`
   and the name-built `track:` ids are gone. Tests: `ListenBrainzIdsTests`, `Every_event_id_is_one_kind_and_one_value`.
8. **Terms applied:** one call per second with a 1.2 s floor; wait out the window at 2 calls left; User-Agent `name/version (contact URL)`;
   no schedule on the hour mark; no polling (the re-sync and the full import are off by default); HTTPS only; no scraping of anything beyond the
   owner's own listens and the ids the owner's library already holds.

## 8. Open items for the owner

- Sign back in at `localhost:8888` (each restart of the dev environment signs the pane out) and the in-app rows (P1-P15, B3, X6, Q) can be run.
  No live ListenBrainz traffic is needed for most of them.
- The block: leave ListenBrainz alone for a while. The plugin now holds itself for an hour after a browser check; the first real call after that is
  one small request.
- Nothing is committed in either repo.
