# The Chronicle plugin checklist: design, build, release, verify

Started 2026-11-05 after the IGDB plugin shipped with its type present but its pages unchecked. Extended 2026-11-06 with
everything the Steam and IGDB work taught. **This is the one checklist for any new plugin or media type.** The owner
should never have to find out what was missed: every line below is answered, in writing, in the plugin's design doc
and again at release, with evidence.

## 0. How to use it

1. **Copy section 12 (the run record) into the plugin's design doc** before designing. Every row gets one status:
   - **Designed**: the design section that covers it is named.
   - **Built**: the code and the test that cover it are named.
   - **Verified in app**: what was seen on screen, in the running app, is stated (not what the code says).
   - **Owner action**: only the owner can do it (enter a credential, sign in, approve files). It says exactly what, where.
   - **N/A**: with the reason. A bare "N/A" is a failure.
2. **Three gates, each a review of the whole record, not a skim:**
   - **G1 design gate:** before any code. Every row is Designed, Owner action or N/A.
   - **G2 release gate:** before tagging. Every row is Built or N/A; tests are green; the settings text has been read aloud.
   - **G3 deploy gate:** after deploy. Every UI row is Verified in app (section 11). The record is closed only now.
3. **Claims are checked before they are made.** "X is not on the platform", "the key is wrong", "it is deployed": each
   was once said without looking and was wrong (Warcraft II; the Steam 403). Look first, then say.
4. **The owner enters their own credentials.** Never ask for one in chat, never write one to a file, never type one
   into a form for them. Say where the box is.
5. **Work only inside the plugin's repo** (`W:\Scripts\<repo>`), never the root of `W:\`. Throwaway build output goes in a
   git-ignored path inside the repo or the scratchpad.

## 1. Sources: measure before designing

| ID | Check | Why |
|---|---|---|
| S1 | Every endpoint the design depends on has been **called for real** and its response recorded as a fixture (secrets, account ids, names removed) | Designs from documentation were wrong: Steam `GetAppList` is gone, the Steam wishlist shape differed, `appreviews` is a redirect |
| S2 | For an unofficial source, the design says plainly what is a community claim and not yet measured, and the first build step is a probe that settles it | A plugin built on guesses ships guesses |
| S3 | Each limit is measured: response sizes, paging, rate ceiling, token lifetimes | Steam store about 200 requests per 5 minutes; one Steam library is one 134 KB response |
| S4 | **Every field the source returns is inventoried** and given a destination (an ImportedLibraryEntry field, the item's metadata record, the account record, or "kept raw") | The owner's rule: grab everything a source has |
| S5 | Terms-of-service and account risk are stated to the owner before they enter anything | Unofficial APIs can break or ban |

## 2. Data: lossless

| ID | Check | Why |
|---|---|---|
| D1 | **No cap, trim, truncation, filter or sampling** of anything a source provides. Hashed or relative paths are resolved, not dropped | Standing owner instruction. The only exclusions are ones the owner named (Steam hardware on a wishlist) |
| D2 | The platform's complete record is stored raw (`RawJson`) beside the mapped fields | A later feature can use what today's mapper ignores |
| D3 | Non-game or odd content (DLC, demos, soundtracks, themes, apps) is kept and tagged, not discarded; an exclusion needs an owner decision recorded in the design | Same rule |
| D4 | Nothing is ever deleted because the source stopped returning it. A sudden drop is reported, never applied | Private profiles and outages return empty lists |
| D5 | Account-level facts (level, trophies, badges, display name) are kept on the account record, not on an item | Steam level and badges |

## 3. Ids and matching

| ID | Check | Why |
|---|---|---|
| I1 | Every id the plugin writes carries its source prefix (`steam:`, `psn:`), in the import half **and** the metadata half | A bare number collides across platforms (owner decision) |
| I2 | The plugin **accepts** both prefixed and bare forms and URLs wherever an id is typed (Fix Match, `GetAcceptedCrossRefPrefixes`) | Old rows and pasted links |
| I3 | Id comparison is on the bare value | `steam:32630` and `32630` once made duplicates |
| I4 | Other ids the platform knows are offered as `AdditionalIds` so another plugin can resolve without a search. This is data, never a dependency | Steam id read by IGDB |
| I5 | **Automatic matching is exact or absent**: exact normalised title, plus a year check when the year is known. Only an interactive search (Add Media, Fix Match) lists similar titles | Loose matching put Guardian of Warcraft on Warcraft |
| I6 | Editions and remasters never merge by name alone; ambiguity yields two items, never a guess | Persona 5 and Persona 5 Royal |
| I7 | A wrong match can be undone (`DELETE /media/{id}/external-ids/<plugin id>`) and the design says how | Five bad matches had to be cleared |

## 4. Images

| ID | Check | Why |
|---|---|---|
| M1 | Every image URL offered **exists** (verified by HEAD, or taken from the record's own path). A guessed URL is never offered unchecked | Guessed Steam art 404ed for some games |
| M2 | The image host allowlist is exact and includes every host the record's own paths resolve to | A missing host silently drops images |
| M3 | Every image kind returned maps to a slot in `imageExtractor.ts` | An unmapped kind is dropped silently |
| M4 | A logo or wide image keeps transparency (PNG), is shown whole, and sits on a ground chosen by measuring its brightness | JPEG logo was solid black; light-on-light vanished |
| M5 | Declared fields (`poster_url`, `backdrop_url`, `logo_url`, `banner_url`, `thumb_url`) are all filled when the source has the art | The owner asked why paths were left unresolved |

## 5. Credentials and settings

| ID | Check | Why |
|---|---|---|
| C1 | Secrets are password-type settings, redacted in logs, exceptions and test output, never in URLs unless the platform forces it | Basic hygiene |
| C2 | **Each setting says in plain words what to enter, where to find it, with a worked example, and what NOT to enter.** The owner reads it as a stranger would | The Steam account box got a display name; the owner asked for "incredibly obvious" |
| C3 | No assumption about the format or length of an id the owner pastes unless measured | "We don't know it is 17 digits" |
| C4 | A rejected credential is **one** call: `PluginAuthException`, one latch, cleared when settings change; the message tells the owner what to do | Looping on a bad key |
| C5 | An auth failure is told apart from a per-item refusal (Steam: HTML 401/403 = bad key; JSON 403 = game not owned or profile private) | The key was latched wrongly |
| C6 | Short-lived tokens refresh single-flight and the refreshed values are returned (`GetRefreshedSettings`) so they survive a restart | Otherwise the owner re-enters daily |
| C7 | The settings text names the realistic failure states the plugin can be in and what each message means | "profile is private", "npsso expired" |

## 6. Import behaviour (host contract)

| ID | Check | Why |
|---|---|---|
| H1 | `GetLibraryAsync` returns one `ImportedLibraryEntry` per title with `Platform`, `StoreSource`, playtime (null = unknown, 0 = never), `LastPlayedAt` (null = never), subscription flag, tags, raw record | Ownership keys on (user, source, id) |
| H2 | Re-running an import changes nothing it has no reason to change: status set only on creation, play events only for a newer last-played time, no `MarkedAsWatched` | Idempotence |
| H3 | A failing library step **fails the task visibly** (the orchestrator rethrows); a pause (429, no response) is `HttpRequestException` and burns no retry | Quiet failure looked like success |
| H4 | A deleted item leaves its ownership row with no item; a re-import does not resurrect what the owner deleted without saying so | Tombstones |
| H5 | Playtime and ownership appear on the item page (`/media/{id}/ownership`) with the right wording for the type | Done once, reused |
| H6 | Series information (`belongsToCollection {id, name, url}`) is supplied when the source has it; with none, the item stays top level. A one-item collection is still a collection | Owner decisions |
| H7 | Pacing: shared limiter and breaker in a static hub (one plugin class is two registry instances), 429 opens the breaker, 5xx backs off | One state for both halves |

## 7. The type

| ID | Check | Why |
|---|---|---|
| T1 | The type is declared with the same name, display name, verb, unit, labels, cast heading and aliases as every other plugin that declares it | Merged by name at host startup; a mismatch depends on load order |
| T2 | The host was **restarted** after install | Types are created at startup |
| T3 | `SupportsCollections`, `EnrichOnly`, `DefaultPriority` are deliberate, not defaulted | Games group into series |

## 8. Metadata Assignment page

- Every active type is listed, sorted by display name; check the page bottom and the "Choose a media type" dropdown.
- A plugin appears on a field only if it **declares** a canonical field name (`MetadataResolutionService.FieldMap`).
  `extended_data` is not resolvable.
- **Declare every field the plugin really fills.** A mapper test asserts each declared field is filled and each filled
  field is declared.
- A saved assignment config predates the plugin: the page appends the plugin to the end of each field. Move it to the
  front where an older plugin is weaker (IMDb for games). Do it, or tell the owner. Wikipedia goes last in every list.
- **Plugins stand alone.** Nothing in the plugin requires another plugin's install, data or build order.

## 9. Library, Add Media, Fix Match, item page

- The Library lists only types that have at least one item: import, then look. Status words must read sensibly
  ("Unwatched" for a game is a UI note).
- Add Media lists the type; a search returns results with the plugin's key and cover. Fix Match accepts the plugin's ids.
- Item page: cast section headed by `CastHeading`, populated (developers with their logos); crew populated; the plugin's
  metadata box shows its record; the ownership line; the Collection badge where a series exists.

## 10. Packaging and release

- `plugins.json` entry (tags include the type), embedded fallback copy, `RunTestEnvironment.ps1` build entry,
  `Deploy-Plugin.ps1` entry, `docs/plugins/PLUGIN_*.md`, `docs/PLUGIN_CATALOGUE.md`, `BACKLOG.md`.
- Version bumped in manifest and csproj together; `Version` is read from the assembly; a test compares it to the manifest.
- GitHub release zip carries the DLL, deps.json and manifest. Tagged `vX.Y.Z`.
- A hot deploy needs the owner to approve the changed files on the Plugins page (integrity check). A deploy is not
  finished until approved and the new version shows.
- Fixtures contain no secret, token, real account id or personal name. Grep for them before the commit.

## 11. The verification pass (G3, do not skip)

In the signed-in browser pane (the owner signs in; I do not enter their password), look at and report what is seen:
Plugins page (card, settings text, Test, tasks, version); Metadata Assignment (type, fields, order); Library (the type
appears after the import runs, collections badged); Add Media (search result with key and cover); one item's page
(cast with logos, crew, metadata box, images, ownership line); Background Tasks (the import ran, no error). If the pane
is signed out, say so and list what is therefore unverified; never write "verified".

## 12. Run record (copy into the plugin's design doc)

| ID | Item (short) | Status | Where / evidence |
|---|---|---|---|
| S1 | Endpoints called for real, fixtures kept | | |
| S2 | Unofficial claims labelled, probe first | | |
| S3 | Limits measured | | |
| S4 | Every returned field inventoried with a destination | | |
| S5 | Terms and account risk told to owner | | |
| D1 | No cap, trim or filter | | |
| D2 | Raw record kept | | |
| D3 | Odd content kept and tagged | | |
| D4 | Never delete on absence; drops reported | | |
| D5 | Account-level facts kept | | |
| I1 | Prefixed ids, both halves | | |
| I2 | Accepts prefixed, bare and URLs | | |
| I3 | Bare comparison | | |
| I4 | AdditionalIds, no dependency | | |
| I5 | Automatic matching exact or absent | | |
| I6 | Editions never merge by name | | |
| I7 | Wrong match can be undone | | |
| M1 | Image URLs verified | | |
| M2 | Image host allowlist | | |
| M3 | Image kinds map to slots | | |
| M4 | Logos: PNG, whole, measured ground | | |
| M5 | All art fields filled | | |
| C1 | Secrets hidden and redacted | | |
| C2 | Settings text obvious, with example | | |
| C3 | No format assumptions | | |
| C4 | Rejected credential costs one call | | |
| C5 | Auth failure vs item refusal | | |
| C6 | Token refresh single-flight, persisted | | |
| C7 | Failure states named in the text | | |
| H1 | One entry per title, full fields | | |
| H2 | Idempotent import | | |
| H3 | Failure visible; pause burns no retry | | |
| H4 | Tombstones | | |
| H5 | Ownership shown on the item | | |
| H6 | Series info; top level when none | | |
| H7 | Shared limiter and breaker | | |
| T1 | Type declared identically | | |
| T2 | Host restarted | | |
| T3 | Type flags deliberate | | |
| MA | Metadata Assignment: fields, order, standalone | | |
| LB | Library, Add Media, Fix Match, item page | | |
| PK | Packaging and release items | | |
| VP | Verification pass done | | |
