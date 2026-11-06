# PlayStation Network plugin (`Chronicle.Plugin.PlayStation`): design

2026-11-06. Status: **designed, not built, nothing measured yet.** Sony's sign-in was overloaded when the owner tried
the account, so no live call has been made. Every Sony fact below is a community claim (the `psn-api` and PSNAWP
libraries) and is labelled **[unverified]** until the probe in section 3 settles it. Governed by
`docs/PLUGIN_NEW_TYPE_CHECKLIST.md`; its run record is section 15 here. Builds on `2026-11-05-game-library-sync-design.md`
(host contract, ownership table, importer) which already exists and needs no host change for this plugin.

## 1. What the owner gets

Their PlayStation games in Video Games, one item per game, merged with the same game from Steam when it is the same
game: platform (PS3/PS4/PS5/Vita), hours played, last played, trophy progress, the trophy list with icons and rarity,
the game's own art, and the account's trophy level. Owned-but-never-played games where Sony says so. It works with
nothing else installed.

## 2. What is known and what is not

| Claim | Source | Status |
|---|---|---|
| No public API; the mobile and web API is reverse-engineered | wrapper docs | [unverified], and Sony can change it |
| Sign-in: the owner's `npsso` cookie is exchanged for an authorization code, then access and refresh tokens | wrapper docs | [unverified] |
| Played titles with playtime and last played: `m.np.playstation.com/api/gamelist/v2/users/me/titles` (paged) | wrapper docs | [unverified] |
| Trophy titles with earned counts: `m.np.playstation.com/api/trophy/v1/users/me/trophyTitles` (paged) | wrapper docs | [unverified] |
| Trophy definitions and the owner's earned flags per title: `.../npCommunicationIds/{id}/trophyGroups/all/trophies` and the `users/me/` counterpart | wrapper docs | [unverified] |
| Trophy summary (level, tier, counts by grade): `.../users/me/trophySummary` | wrapper docs | [unverified] |
| Purchased games: a GraphQL persisted query on `web.np.playstation.com`, PS4 and PS5 only; its hash rotates | wrapper docs | [unverified] |
| Token lifetimes: access about 1 hour, refresh about 2 months, npsso about 2 months | wrapper docs | [unverified] |
| The owner's old account still signs in | owner, today | **unknown** (verification code awaited) |

Rule S2: nothing in sections 5 to 9 is coded until the probe has confirmed it. Where the probe contradicts this
document, the document is corrected first.

## 3. The probe (first build step)

A throwaway console program in `W:\Scripts\Chronicle.Plugin.PlayStation\tools\probe\` (git-ignored output folder inside the
repo), read-only, run by the owner with the npsso in an environment variable they set themselves; the program never
prints it and I never see it. It records, with every token, account id, online id and name replaced by placeholders:

1. Does the npsso exchange work, what do the token responses carry (lifetimes, scopes), what does a rejected npsso return
   (status, body shape)?
2. Each list endpoint above: status, total count, page size honoured, response size, **every field name on one sample
   record**, the date and duration formats, how a never-played title looks, how PS3/Vita/PSP titles appear (if at all).
3. The purchased-games query: does the default hash still work; if not, what does the failure look like.
4. Which image hosts the records point at, and whether those URLs answer HEAD.
5. Whether Sony rate-limits (headers, 429 behaviour) and how many calls the full import costs.
6. Whether subscription (PS Plus) titles are distinguishable.
7. Whether region or language changes titles.

Output: fixtures under `tests/fixtures/live-<date>/` and a "what the probe found" section appended here. Outcomes:
works (build), npsso rejected (owner signs in again), account closed or region-locked (plugin parked; this document
stays, nothing else is lost). The owner is told which one in plain words.

## 4. Authentication

- One setting, **npsso** (password-type): never logged, never in a URL, redacted from every error and test output (C1).
- Exchange and refresh are single-flight behind one lock; tokens are an immutable record; the refreshed refresh-token
  is returned through `GetRefreshedSettings` so a restart does not lose it (C6). Access tokens are refreshed a few minutes
  before expiry, not on a 401.
- A rejected npsso or refresh token is a `PluginAuthException` after **one** call; the latch clears when the settings
  change (C4). The message: "Sony rejected the sign-in code. Sign in at playstation.com, open
  https://ca.account.sony.com/api/v1/ssocookie, copy the 64-character value after "npsso", and paste it here."
- A 401 on a data call after a good refresh means the token was revoked: one refresh attempt, then the same rejection.
- A 403 on one title's trophies (hidden, or no trophy support) is about that title (C5): the title imports without
  them and the health message counts it; it is never an auth latch.
- Terms: unofficial use of Sony's API is against their terms. Read-only calls make an account action unlikely, not
  impossible. The settings text says so in one sentence before the owner pastes anything (S5).

## 5. Data inventory: everything, with a destination (S4, D1)

| Data | Destination |
|---|---|
| Title name, titleId, concept id and name, category (ps4_game, ps5_native_game, ps3, vita, pc ...), service | `ImportedLibraryEntry` (title, id, platform, tags) and `RawJson` |
| playDuration, playCount, firstPlayedDateTime, lastPlayedDateTime | `PlaytimeMinutes`, `LastPlayedAt`, and first played kept in raw (the entry has no field for it; the date goes to `AcquiredAt` only if the probe shows it is the first-play date, which is not acquisition, so it stays in raw) |
| Title images and media from the game list (icon, concept media: screenshots, promo art, videos) | `PosterUrl`, `BackdropUrl`, the rest as the metadata half's images |
| Trophy title: npCommunicationId, name, icon, platform, defined and earned counts by grade, progress, hidden flag, last updated | the metadata half's extended record; `earned/defined` also in raw |
| Every trophy: name, detail, icon, grade, hidden, earned flag and date, global earn rate and rarity, group | the metadata half's extended record, shown on the item page |
| Trophy summary: level, tier, progress, counts by grade | account record via `GetAccountInfoAsync` |
| Profile: online id, avatar, about, languages, PS Plus flag, if the profile endpoint answers | account record |
| Purchased games (PS4, PS5): id, name, platform, entitlement type, subscription flag, dates | `ImportedLibraryEntry` (`AcquiredAt` when the record carries a purchase date) |
| Anything else a record carries | `RawJson` unchanged |

Friends, presence and messages are **out of scope**: they are other people's data and are not the owner's library. This
is a recorded owner-level decision to confirm (D-P2), not a filter on the owner's own data.
Non-game titles (apps, themes, demos, add-ons) are imported and tagged by category, never dropped (D3). The library
screens already separate by tag.

## 6. Import half (`IImportProvider`)

- `GetLibraryAsync` merges the three lists by titleId / concept: a title in more than one list is one entry. Platform
  from the category (PS5, PS4, PS3, Vita, PSP, "PC" for PlayStation PC). A title that is only in the purchased list has
  `PlaytimeMinutes = null` (unknown), never 0 (H1). A title in the game list with zero play has 0.
- `OwnedViaSubscription` only when the probe shows Sony says so; otherwise false, never guessed (documented limit).
- Ids: `psn:<titleId>` for a title, with `AdditionalIds` carrying `psn_concept` and `psn_np` (the trophy communication
  id) so the metadata half and a later re-import find the same record (I1, I4). PS4 and PS5 editions of one concept
  stay separate entries when their title ids differ (they are different products); the host's matcher joins them by
  title and year only when unambiguous (I6).
- `Year` from the record only when it carries a release date; never guessed.
- Status, events and tombstones follow the host importer already built and tested (H2, H4): nothing here sets a status.
- `GetAccountInfoAsync`: display name = online id; raw = trophy summary and profile.
- The wishlist: Sony has no reachable wishlist API in the community docs, so none is imported; recorded, not a filter.
- A failing library list throws, so the task fails visibly (H3); an empty list from a good token, when the last import
  had titles, is reported in health as "Sony returned nothing; nothing was removed" (D4).

## 7. Metadata half (`IMetadataProvider`)

- Standalone: it needs only the owner's npsso, never IGDB or Steam (checklist section 8).
- `GetByIdAsync("psn:<titleId>")` returns the title's record: name, platform, images, the full trophy list and progress
  (when the owner has the title), concept media. The extended record is the trophy data, shown in the plugin's box.
- `SearchAsync`: for an item whose known ids include `psn`, no search (like Steam). For a name, **automatic** matching
  looks only in the owner's own title list for an exact normalised title (and year if both known) (I5): Sony's store has
  no public search the plugin will rely on, so a game the owner does not have cannot be found by name, and the
  interactive search says so in `fixMatchHint` rather than returning guesses.
- Declared fields: `title`, `overview` (only if a record has a description), `year` (if present), `poster_url`,
  `backdrop_url`, `logo_url` only if Sony supplies one, `genres` if present, `cast` and `crew` only if the probe shows
  developer/publisher data; a mapper test asserts declared equals filled (checklist section 8). The type declaration is
  copied verbatim from the Steam plugin (T1).
- `GetAcceptedCrossRefPrefixes`: `psn:`; id parsing accepts `psn:<id>`, a bare id, and a store.playstation.com
  concept or product URL (I2).
- Images: hosts from the probe go on an exact allowlist; every URL is verified by HEAD or taken from the record (M1, M2);
  every image kind has a slot (M3); trophy icons are returned as images of the extended record.
- Series: Sony carries no collection information, so none is supplied; an IGDB-enriched game still groups (H6).

## 8. Matching with other platforms

Same game on Steam and PlayStation shares no id, so the host joins them by normalised title and year. This plugin carries
its own small copy of the normalising rules (strip `™`, `®`, platform suffixes "(PS5)", "PS4 & PS5", "Digital Edition";
fold Roman numerals; **never** strip edition words that name a different product: Royal, Remastered kept as given),
pinned by a shared fixture with the Steam plugin's copy (D7). Ambiguity yields two items; the duplicate scanner offers
the merge. A wrong match can be undone with `DELETE /media/{id}/external-ids/chronicle.plugin.playstation` (I7).

## 9. Resilience and pacing

| Event | Behaviour |
|---|---|
| 429 or no response | `HttpRequestException` with status: the host pauses, burns no retry; breaker opens |
| 5xx | breaker opens 15 s, doubling to 5 min |
| Rejected npsso or refresh | one `PluginAuthException`, latch, message from section 4 |
| Persisted-query hash rejected | the purchased list is skipped, health says "purchases unavailable: Sony changed its query", the import continues with the other two lists. The hash lives in settings with a default (advanced section) so the owner can update it without a release |
| Shape change | tolerant readers; raw record kept; unknown category becomes a tag |
| Paging | page size from the probe; stops on an empty page or the reported total; a loop guard of total/pagesize + 2 |
| Pacing | shared limiter and breaker in a static hub for both halves (H7); rate from the probe |
| Timeouts | 25 s per call; trophy calls run in a low-priority task with its own checkpoint so one slow title cannot hold the library |

## 10. Settings text (C2, C7), drafted now so it can be reviewed

1. **Notice 1:** "This plugin signs in as you using Sony's unofficial web sign-in. Sony does not offer a public API and its
   terms do not allow this, though read-only use is unlikely to cause trouble. Continue only if you accept that."
2. **npsso (password):** "A sign-in code, not your password. Sign in at https://www.playstation.com in this browser,
   then open https://ca.account.sony.com/api/v1/ssocookie. You will see `{"npsso":"abc...64 characters"}`. Paste only the
   64 characters inside the quotes. It lasts about two months; when it stops working this page will say so."
3. **Region (advanced)** and **language**.
4. **Purchased-games query hash (advanced):** "Leave blank. Only change this if the plugin reports that Sony changed
   its purchase query."
5. Health messages: "signed in as <online id>", "Sony rejected the code, paste a new one", "purchases unavailable",
   "<n> titles have hidden trophies and were imported without them", "Sony returned nothing; nothing was removed".

## 11. Tasks

`import-library` (daily; titles, playtime, purchases), `fetch-trophies` (weekly, on by default per the owner's rule:
everything), `fetch-missing-metadata`, `resync-all-metadata` (off by default). Checkpoints so a cancelled run resumes.

## 12. Tests (all against probe fixtures, no network)

Token exchange and refresh single-flight; rejected npsso is one call and latches; latch clears on new settings;
redaction (the npsso never appears in any exception or log); each list mapped, every field of a sample kept raw; merge
of the three lists by titleId; never-played is 0 and purchased-only is null; category to platform; hash rotation degrades;
paging guard; an empty result never removes; declared fields equal filled fields; ids prefixed and parsed from bare,
prefixed and URL; automatic matching exact only; image URLs only from the allowlist; manifest version equals assembly
version; settings text contains the example and the "not your password" wording; fixtures contain no token or account id
(a test greps them).

## 13. Packaging

Repo `Chronicle.Plugin.PlayStation`; `plugins.json` entry and embedded fallback; `RunTestEnvironment.ps1` and
`Deploy-Plugin.ps1` entries; `docs/plugins/PLUGIN_PLAYSTATION.md`; `PLUGIN_CATALOGUE.md`; `BACKLOG.md`; release zip with DLL,
deps.json and manifest; hot deploy needs the owner's approval on the Plugins page.

## 14. Owner decisions and actions

| # | Item | Recommendation |
|---|---|---|
| D-P1 | Proceed to the probe once the account signs in | Yes |
| D-P2 | Friends, presence, messages are out of scope (other people's data) | Yes |
| D-P3 | Trophies on by default | Yes (everything) |
| Owner action | Sign in, fetch the npsso, paste it into the plugin's settings box themselves; run the probe with it in an environment variable they set | Never in chat |
| Owner action | Approve the changed plugin files on the Plugins page after deploy | |
| Owner action | Sign in to the browser pane for the verification pass | |

## 15. Run record (checklist section 12)

| ID | Item | Status | Where / evidence |
|---|---|---|---|
| S1 | Endpoints called for real | Designed | section 3 (probe is step one); **nothing called yet** |
| S2 | Unofficial claims labelled, probe first | Designed | section 2 table, rule S2 |
| S3 | Limits measured | Designed | probe item 5; not measured |
| S4 | Every field inventoried | Designed | section 5 |
| S5 | Terms told to owner | Designed | section 4, notice 1 in section 10 |
| D1 | No cap or filter | Designed | section 5 (friends excluded by D-P2 only) |
| D2 | Raw record kept | Designed | sections 5, 6 |
| D3 | Odd content kept and tagged | Designed | section 5 |
| D4 | Never delete on absence | Designed | section 6 |
| D5 | Account facts kept | Designed | sections 5, 6 (`GetAccountInfoAsync`) |
| I1 | Prefixed ids | Designed | section 6 |
| I2 | Accepts bare, prefixed, URL | Designed | section 7 |
| I3 | Bare comparison | Designed | host `Bare()` already in importer |
| I4 | AdditionalIds, no dependency | Designed | section 6 |
| I5 | Exact automatic matching | Designed | section 7 |
| I6 | Editions never merge | Designed | sections 6, 8 |
| I7 | Undo a wrong match | Designed | section 8 |
| M1 | Image URLs verified | Designed | section 7 |
| M2 | Host allowlist | Designed | section 7 (hosts from probe) |
| M3 | Kinds map to slots | Designed | section 7 |
| M4 | Logos PNG, whole, ground | N/A for now | applies only if Sony supplies a logo; the card already handles it |
| M5 | All art fields filled | Designed | section 7, only where Sony has the art |
| C1 | Secrets hidden | Designed | section 4 |
| C2 | Settings text obvious | Designed | section 10 (drafted, to be re-read at G2) |
| C3 | No format assumptions | Designed | npsso length is stated as observed, not enforced; probe confirms |
| C4 | One call on rejection | Designed | section 4 |
| C5 | Auth vs item refusal | Designed | section 4 |
| C6 | Token refresh persisted | Designed | section 4 |
| C7 | Failure states named | Designed | section 10 item 5 |
| H1 | Entry per title | Designed | section 6 |
| H2 | Idempotent | Designed | host importer, tested (2260 unit tests) |
| H3 | Failure visible | Designed | sections 6, 9 |
| H4 | Tombstones | Designed | host importer |
| H5 | Ownership on item | Designed | host, already built |
| H6 | Series info | N/A | Sony has none; IGDB supplies it |
| H7 | Shared limiter | Designed | section 9 |
| T1 | Type identical | Designed | section 7 |
| T2 | Host restarted | Owner/me at deploy | G3 |
| T3 | Type flags deliberate | Designed | copied from Steam |
| P1 | Metadata Assignment: type listed | Designed | `game` already exists via Steam/IGDB/IMDb; declaration copied verbatim (T1). G3: look at the page bottom and dropdown for "Video Games" |
| P2 | Plugin under every field it fills | Designed | section 7 declares fields; test asserts declared equals filled. G3: open each field of Video Games and find "PlayStation" under exactly those |
| P3 | Order within each field | Designed | New plugin lands last in saved configs. Decision for the owner: PlayStation ahead of IMDb, below IGDB for fields IGDB fills (IGDB is the richer source), above for trophy-only data. I will set it and say so, not leave it |
| P4 | Library: section, labels, collections | Designed | Video Games section exists once any game is imported; PlayStation items join existing games by title and year; G3: look at the count after import and at badges |
| P5 | Library: tile, art fallback | Designed | art verified by HEAD (M1); an item without art shows initials; G3 |
| P6 | Add Media: type offered, results | Designed, with a limit | PlayStation appears in results only for games in the owner's own list (section 7); the hint says so; G3: search a game the owner has and one they do not |
| P7 | Add Media: item lands with prefixed ids | Designed | `psn:` ids; G3 |
| P8 | Fix Match: ids and hint | Designed | section 7 parsing; G3: paste `psn:<id>`, bare id and a store URL |
| P9 | Add Collection, Scan, Getting Started | Designed | a library-import type has no folders to scan; G3 confirms no PlayStation entry is expected there |
| P10 | Media Types: one row | Designed | identical declaration; G3: no second "game" type |
| P11 | Plugins page | Designed | settings text section 10; Test button makes one safe call (token exchange plus one list page); tasks section 11; approval after deploy is an owner action |
| P12 | Item page: cast, crew, box, images | Designed | cast/crew only if the probe shows developer data; the box shows the trophy record; trophy icons mapped (M3); G3 |
| P13 | Item page: ownership, collection, status | Designed | ownership line per platform already built; PlayStation row shows hours, last played; G3 |
| P14 | Person pages, search, dashboard rows | Designed, gap | developer pages exist only if the probe shows developer data; search and dashboard rows are host behaviour for games already checked on Steam; G3 re-checks with a PlayStation-only game |
| P15 | Background Tasks | Designed | section 11, checkpoints, visible failure; G3 |
| SA | Stands alone at every place | Designed | section 7; G3 includes reading a PlayStation game with Steam and IGDB data absent |
| PK | Packaging | Designed | section 13 |
| VP | Verification pass | Not yet | G3, after build; needs the owner's sign-in |

## 16. Review of this design against the checklist (G1)

1. **Everything still hinges on the probe.** No row above is Built or Verified, and the document says so. Fixed: sections 2
   and 3 make the probe the only next step.
2. **First-play date is not acquisition date.** An early draft mapped `firstPlayedDateTime` to `AcquiredAt`, which would
   record a purchase that did not happen then. Fixed: it stays in raw unless the probe shows a purchase date.
3. **Purchased-only titles would show "0 minutes".** That reads as "played for nothing". Fixed: null for unknown.
4. **A rotated query hash would fail the whole import.** Fixed: degrade with a visible message and an editable hash.
5. **A hidden-trophy 403 could latch the credentials.** Fixed: per-title refusal is not auth (C5).
6. **Store search was assumed.** Nothing says Sony offers a stable public search; the design restricts automatic matching to
   the owner's own list and states the limit in the Fix Match hint instead of promising a lookup.
7. **Friends excluded.** That is the one exclusion; it is stated as an owner decision to confirm, not buried.
8. **Not covered and why:** PlayStation Plus catalog membership (no known endpoint; only if the probe shows a flag),
   PS3/Vita/PSP purchases (the community docs say unreachable), a wishlist (no known endpoint). Each is a stated limit.
