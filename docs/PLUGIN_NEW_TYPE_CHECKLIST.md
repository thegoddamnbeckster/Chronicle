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

## 8. Every place a media type or plugin must show up (one numbered check per place)

Each row is looked at **in the running app** after the host restart (T2) and the first import. "Looked at" means the
run record names what was seen: the text on screen, the count, the position in the list. The first three are the ones
that have already been missed once.

| ID | Place | What must be true | How it silently goes wrong |
|---|---|---|---|
| P1 | **Settings > Metadata Assignment**, the type list | The type is listed under its display name. The list is sorted by display name, so check the **bottom** of the page, and the "Choose a media type" dropdown at the top | A type starting with V sits below the fold and looks missing |
| P2 | Metadata Assignment, each field of the type | The plugin appears under **every field it fills** and under none it does not (a mapper test asserts declared equals filled). Only canonical field names count (`MetadataResolutionService.FieldMap`); `extended_data` is not resolvable | A field not declared has no plugin to pick; IGDB declared no cast, so developers were empty |
| P3 | Metadata Assignment, the order inside each field | A saved assignment config predates the plugin, so the page appends it last. Move it ahead of any weaker plugin (IMDb for games), Wikipedia last. Do it, or tell the owner it needs doing | The new plugin exists but never wins |
| P4 | **Library**, the type section | The type appears after the first item exists (the Library hides empty types). Display name, hierarchy label, item count, sort and filters read sensibly. Collections are badged; items without a series are top level | New type invisible until an item exists; "Watching" on a game |
| P5 | Library, an item tile | Poster, title, year, status and (for games) ownership read correctly; an item with no art shows a deliberate fallback, not a broken image | Missing or 404 art |
| P6 | **Add Media**, the type picker | The type is offered, with the plugin's search results showing the plugin's key and cover, and the right candidate first (strongest match on top) | Type absent, or results from the wrong plugin |
| P7 | Add Media, a result added | The new item lands in the Library under the type with the plugin's ids, prefixed | Bare ids collide across platforms |
| P8 | Fix Match | Accepts the plugin's ids bare, prefixed and as URLs; the `fixMatchHint` says which | Owner cannot correct a bad match |
| P9 | Add Collection, Scan (folders by type), Getting Started | The type is offered where types are chosen, or the check says why not (a library-import type has no folder to scan) | Type missing from a dropdown nobody thought of |
| P10 | **Settings > Media Types** | One row for the type, unit, verb, labels, cast heading, aliases as declared; no duplicate type from a spelling variant | Two plugins declared it differently |
| P11 | **Plugins** page | Card shows version, settings with their text, Save, Test, scheduled tasks seeded, and the type badge; changed files approved (integrity) | Old version still loaded |
| P12 | Item page: cast, crew, box, images | Cast is headed by `CastHeading` and populated (developers with logos); crew populated; the plugin's metadata box shows its record; every image kind lands in a slot | Empty sections |
| P13 | Item page: ownership, collection, status | Ownership line per platform (hours, last played, subscription), Collection badge where a series exists, no "Reset Watch Progress" for a "played" type | Wording for the wrong media kind |
| P14 | Person pages, search, dashboard rows | A developer or studio page lists the plugin's items under the type; global search finds them; any "recent" or "continue" row treats the type correctly | Type works in the Library but nowhere else |
| P15 | Background Tasks | The plugin's tasks ran, show "Waiting..." when paused and a visible error on failure | Silent failure |

Also true at every place above: **the plugin stands alone.** Nothing in it requires another plugin's install, data or
build order, and each place still works when only this plugin is installed (check by reading the item with the other
plugins' data absent).

## 8a. Identity and branding (the manifest)

| ID | Check | Where it shows | How it goes wrong |
|---|---|---|---|
| B1 | `iconUrl` is a real address that answers (a favicon or logo), loaded over HTTPS, and is legible at 16 to 20 px on **both a light and a dark card** | Plugins page card, the plugin fold and metadata box on every item page, Get Plugins list | A 404 hides the icon (the UI removes a broken image silently); a dark icon vanishes on a dark theme |
| B2 | `brandColorLight` and `brandColorDark` are both set, each with at least 3:1 contrast against the card backgrounds of **every installed theme** it is meant for (compute it; do not eyeball) | Background Tasks card border and tint | Absent colour falls back to the default accent, so the plugin looks unbranded; a low-contrast colour makes the border invisible |
| B3 | The host actually uses both colours: the Background Tasks card reads `brandColorDark` only today. Look at a light theme; if the dark colour is used there, that is a host gap to fix, not a plugin fault | Background Tasks page | `brandColorLight` is declared and never read |
| B4 | `name`, `description` and `author` are the text the owner should read: what it imports, what it needs (a key, a public profile), that it works alone, and its main limit | Plugins page, Get Plugins | A description written for developers |
| B5 | `fixMatchHint` names every form the plugin accepts | Fix Match panel | Owner cannot tell what to paste |
| B6 | Every background task has a plain display name, a description of what it does and its cost or limit, a sensible default schedule and default on/off, a `run_confirmation` message for anything large, and `schedulable` set deliberately | Background Tasks page, Plugins page | A "Run now" that silently starts a 10-minute job |

## 8b. Plugin-specific settings

| ID | Check | How it goes wrong |
|---|---|---|
| X1 | Every setting is declared in `GetSettingsSchema` with key, label, type (`Text`, `Password`, `Number`, `Boolean`, `Dropdown`, `MultiSelect`, `Url`, `TextArea`, `Notice`), `Required`, and a `DefaultValue` for every optional one. The manifest's own `settings` list says the same or is empty on purpose | Two lists drift; a Required setting has no default and the first save fails |
| X2 | Each setting's help text answers where, what, an example, and what not to enter (C2), and the field order reads top to bottom as the owner would do it | Owner enters the wrong thing |
| X3 | Anything sensitive is `Password`; anything the owner should read first is a `Notice` | Secret shown in clear |
| X4 | Values are validated when read (`Settings.From`): a bad value falls back to the default or is refused with a message, never crashes and never reaches the wire | A pasted newline in a key |
| X5 | Advanced settings (regions, hashes, pacing) are labelled advanced, default to a working value, and say "leave blank unless told" | Owner tunes what they should not |
| X6 | Saving settings, restarting, and saving again round-trips: a stored value comes back, a password is not echoed, an unchanged password is not wiped by a save | Saving the form clears the key |
| X7 | `Configure` is idempotent and clears every latch and cached token (C4) | Old failure sticks after fixing the setting |
| X8 | The **Test** action makes one safe, read-only call and reports in words: signed in as X, key rejected, profile private | A Test button that always says OK |
| X9 | A setting that changes behaviour in the field (include wishlist, include non-game apps) states what changes and its default, and the default is the owner's stated preference (everything on) | Hidden filtering |
| X10 | A test reads the schema and asserts: every key is read by `Settings.From`, every setting has help text, every secret is a `Password`, defaults parse | A declared setting nothing reads |

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
every row P1 to P15 of section 8, in that order, writing down what was seen for each. If the pane
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
| P1 | Metadata Assignment: type listed (bottom of page, dropdown) | | |
| P2 | Metadata Assignment: plugin under every field it fills, none it does not | | |
| P3 | Metadata Assignment: order within each field | | |
| P4 | Library: type section, labels, collections badged | | |
| P5 | Library: item tile, art fallback | | |
| P6 | Add Media: type offered, results with key and cover, best first | | |
| P7 | Add Media: added item lands with prefixed ids | | |
| P8 | Fix Match: ids and hint | | |
| P9 | Add Collection, Scan, Getting Started | | |
| P10 | Settings > Media Types: one row, no duplicate | | |
| P11 | Plugins page: card, text, Test, tasks, approval | | |
| P12 | Item page: cast, crew, box, images | | |
| P13 | Item page: ownership, collection, status wording | | |
| P14 | Person pages, search, dashboard rows | | |
| P15 | Background Tasks | | |
| SA | Plugin stands alone at every place above | | |
| B1 | Icon answers, legible on light and dark | | |
| B2 | Brand colours light and dark, contrast computed | | |
| B3 | Host reads both colours (look at a light theme) | | |
| B4 | Name, description, author in the owner's words | | |
| B5 | fixMatchHint lists every accepted form | | |
| B6 | Background tasks: names, descriptions, schedule, confirmations | | |
| X1 | Settings declared fully, schema and manifest agree | | |
| X2 | Help text: where, what, example, what not | | |
| X3 | Secrets are Password; Notice for the first read | | |
| X4 | Values validated on read | | |
| X5 | Advanced settings labelled, working defaults | | |
| X6 | Save, restart, save round-trips; password not wiped | | |
| X7 | Configure idempotent, clears latches | | |
| X8 | Test action: one safe call, plain result | | |
| X9 | Behaviour switches state what changes; defaults are "everything on" | | |
| X10 | Schema test: keys read, help present, secrets typed, defaults parse | | |
| PK | Packaging and release items | | |
| VP | Verification pass done | | |
