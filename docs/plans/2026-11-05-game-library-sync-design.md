# Game library sync: Steam, PlayStation, Xbox, Nintendo

Date: 2026-11-05
Status: **design, reviewed once (section 12). Nothing built.**
Owner's accounts: Steam active; PlayStation Network old (may still exist); Microsoft/Xbox unknown; Nintendo none.

## 1. Why this exists

The IGDB plugin gives metadata for a game Chronicle already knows. It cannot say which games the owner has. The
owner wants their libraries to arrive on their own: Steam, PlayStation Network, Xbox, and Nintendo if possible.
Importing IGDB's list CSV by hand was rejected as clumsy. Each platform gets its own plugin, because a platform
account is a different credential, a different failure mode and a different release cadence.

Rules carried over from earlier work:

- Each plugin stands alone. None needs IGDB, another platform plugin, or any build order. If IGDB is installed,
  the existing enrichment pipeline improves the items afterwards; if not, the item is still complete from the
  platform's own data.
- Lossless: keep everything the platform returns for a title and for the user's relationship to it. No caps, no
  filtering. The raw record goes into the item's metadata under the plugin id.
- Failure behaviour follows the IGDB plugin: rate limits and outages pause, they never burn retries or mark
  items failed; rejected credentials cost one call, not one per item.

## 2. What Chronicle's import contract gives us, and what it lacks

Verified from `IImportProvider.cs` and `SyncOrchestrationService.cs`.

| Need | Contract today | Gap |
|---|---|---|
| Games the user owns | `GetWatchHistoryAsync` returns `ImportedWatchEvent`s: a play at a time | An owned, never-played game has no event. Passing a fake one would record a play that did not happen |
| Playtime | `InteractionEvent` has a timestamp and a progress percent, no duration | Nowhere to put hours played |
| Last played | the event timestamp | Fine for "last played"; wrong as "played once" |
| Platform (PC, PS5, Switch) | none | Needed to say where a game is owned |
| Achievements / trophies | none | Lossless rule says keep them |
| Status | `UpsertLibraryStatusAsync` sets Completed on any play, never overwrites a user-set status | Completed is wrong for a game with 3 minutes on the clock |
| Matching | own external id, then `AdditionalIds`, then title+year, then a stub | Works for games unchanged. `game` has no `ImportShape`, so the flat path is used, which is correct |
| Ratings / watchlist | exist | Steam wishlist maps to the watchlist; the platforms have no ratings |

Conclusion: the contract needs one addition, a **library entry** (section 4). Everything else is reused.

## 3. Measurements and what could not be measured

Live probes run today with no credentials:

| Probe | Result | Consequence |
|---|---|---|
| Steam `appdetails?appids=1245620` | 200, 29 KB, full record | Public, no key. Full store record for stubs |
| 12 `appdetails` calls back to back | all 200 | The documented ~200 per 5 minutes is the real ceiling; pace to it |
| `IPlayerService/GetOwnedGames` with no key, and with a junk key | 401 `Access is denied. Retrying will not help` | A key is mandatory. A bad key looks identical to no key |
| `ISteamApps/GetAppList/v2` | **404 Method not found** | The existing `PLUGIN_STEAM.md` design names an endpoint that no longer exists. Do not use it |
| `IStoreService/GetAppList/v1` without a key | 403 | Replacement exists, also key-gated. Not needed: owned games come with names |
| `IWishlistService/GetWishlist/v1?steamid=` without a key | 200 `{"response":{}}` | Works key-less. An empty body is ambiguous: empty wishlist and private wishlist look the same |
| `steamcommunity.com/profiles/<id>/games?xml=1` | 302 to login | The old key-less XML route is gone |
| Game Pass catalog `catalog.gamepass.com/sigls/v2` | 200, 11 KB, product ids | Public. It lists what Game Pass offers, not what the user owns |

**Not measurable until the owner supplies credentials:** the Steam owned-games response, the private-profile
response, every PlayStation call, every Xbox call. Section 14 lists what is needed. The design marks each such
claim "unverified" and the build plan starts with a probe step, as IGDB did.

## 4. Host change: library entries

Add to `Chronicle.Plugins`, default-implemented so existing plugins are untouched (same pattern as
`GetCreditsAsync`):

```csharp
public record ImportedLibraryEntry(
    string ExternalId,                       // "steam:1245620", "psn:CUSA12345_00", "xbox:1234567890"
    IReadOnlyDictionary<string,string> AdditionalIds,
    string MediaType,                        // "game"
    string Title,
    int?   Year,
    string Platform,                         // "PC", "PS5", "Xbox Series", "Switch" ...
    string StoreSource,                      // "steam", "psn", "xbox"
    int?   PlaytimeMinutes,                  // null = the platform does not say, 0 = owned, never played
    DateTimeOffset? LastPlayedAt,
    DateTimeOffset? AcquiredAt,
    bool   OwnedViaSubscription,             // Game Pass / PS Plus: access, not ownership
    string RawJson);                         // the platform's whole record for this title, kept as-is

Task<List<ImportedLibraryEntry>> GetLibraryAsync(CancellationToken ct = default) => empty;
```

Host side, in `SyncOrchestrationService`:

1. New step after history: for each entry `MatchOrCreateAsync` (a thin adapter builds the event shape the matcher
   already understands), then upsert.
2. New table `user_game_ownership` (user, item, source, platform, playtime, last played, acquired, via-subscription,
   raw json, updated at). One row per user, item and platform. This is where a game owned on two platforms stays
   one item with two rows. `UserLibrary.StartedAt` is set from the earliest known play.
3. Status rules, never overriding a user-set status (same guard as today):
   - playtime 0 or null: `Unwatched` ("owned, not started"), not `PlanToWatch`
   - playtime above 0: `Watching`
   - `Completed` only from an explicit signal (see achievements, 5.1) or the user
4. Playtime is **stored and replaced**, not appended as events. A sync never creates an `InteractionEvent` per run.
   A play event is created only when `LastPlayedAt` is newer than the newest stored one, so history grows with real
   plays and re-syncing is idempotent.
5. The raw record is kept in the item's metadata under the plugin id. A plugin's own enrichment (the Steam store
   record) is in the plugin's metadata provider, see 5.1.

Open decision D1: new table versus columns on `UserLibrary`. Recommendation: the table, because platform is a
dimension and the owner has said they own games on several platforms.

## 5. Per-platform design

### 5.1 Steam (`Chronicle.Plugin.Steam`) — build first

The owner has an active account and the API is documented and stable, so this is the one that can be fully
measured and tested.

Auth: a Steam Web API key (free, steamcommunity.com/dev/apikey) and the owner's SteamID64. No OAuth, no expiry.
The 401 for a bad key is permanent ("Retrying will not help"), so it maps to `PluginAuthException` once, latches,
and clears on the next Configure, like the IGDB credentials latch.

Library: `IPlayerService/GetOwnedGames/v1` with `include_appinfo=1&include_played_free_games=1`. One call returns
every owned app with `playtime_forever`, `rtime_last_played`, and per-platform playtime fields. One call, no
paging, so no delta needed; a full pull every run is cheap and is correct. Keep the whole object as `RawJson`.

Private profile: Steam answers with an empty `response` rather than an error (unverified). The plugin must tell
this apart from a genuinely empty library: an empty result with a configured key and a nonzero previous count is
reported as "profile or game details are private", never as "library is empty", and never triggers deletions.
Nothing is ever removed because it vanished from a response.

Metadata for stubs, `GetItemMetadataAsync` and `IMetadataProvider` both: `store.steampowered.com/api/appdetails`
(measured 29 KB for one game). Fields: name, type, `about_the_game`, `detailed_description`, developers,
publishers, genres, categories, release date, `required_age`, platforms, metacritic, screenshots, movies,
`dlc`, `price_overview`, `supported_languages`, `content_descriptors`, `package_groups`, background, header,
capsule images. All kept. `type` is checked: `dlc`, `demo`, `music`, `video`, `advertising`, `hardware`, `tool`
are kept as items only when the owner owns them, tagged with their type, because the owner's library does
contain soundtracks and demos and "no filtering" applies. Pace: one shared limiter at 200 per 5 minutes, a breaker
that opens on 429 or 403 and doubles from 2 s to 5 min. A 307-game first import takes about 8 minutes of store
calls, which is acceptable as a background task; the library rows land first, stubs fill in behind.

`appdetails` returns `{"<id>":{"success":false}}` for delisted apps (common in a long-lived library). That is
terminal for metadata only: the entry still imports from `GetOwnedGames`, which carries the name and icon.

Achievements: `ISteamUserStats/GetPlayerAchievements` and `GetSchemaForGame`, one call per game (key required).
Not part of the first library pass; a separate low-priority task so a 300-game library does not cost 600 calls up
front. Needs `Completed` rule: all achievements unlocked is the only automatic `Completed` signal.

Wishlist: `IWishlistService/GetWishlist/v1` (key-less, measured) feeds `GetWatchlistAsync` as `PlanToWatch`.

Ids: `steam:<appid>` own id, plus an additional id the IGDB plugin already understands (`steam:<appid>` in its Fix
Match parser), so enrichment of a Steam-imported game can resolve straight to IGDB with no title search. This is
IGDB reading an id, not a dependency.

Settings: api key (password), SteamID64 (text, with a vanity-url resolver `ISteamUser/ResolveVanityURL`),
language, include wishlist (default on), include non-game apps (default on).

Tasks: `import-library` (daily), `fetch-achievements` (weekly, off by default), `fill-store-metadata` (nightly).

### 5.2 PlayStation Network (`Chronicle.Plugin.PlayStation`) — unofficial

No public API. Everything below is the mobile and web API that community libraries reverse-engineer. Sony can
change it at any time, and the wrapper authors say so themselves.

Auth (unverified live): the owner signs in at playstation.com in a browser and copies an `npsso` cookie value; it
is exchanged for an access code, then access and refresh tokens. The npsso is equivalent to a password, so it is
a password-type setting, never logged, never in URLs, redacted in errors. Token lifetimes are not stated by the
wrapper docs; assumed access about 1 hour, refresh about 2 months, npsso about 2 months. The plugin treats any
auth rejection as `PluginAuthException` and says "paste a new npsso"; it never loops.

Library, per the wrapper's function list: played games with playtime and last played (mobile `gamelist`), trophy
titles with earned counts (`trophy` API), and purchased games (a GraphQL persisted query, **PS4 and PS5 only**, so
PS3, Vita and PSP purchases are not reachable that way). The GraphQL call depends on a persisted-query hash that
Sony rotates. The plugin keeps the hash in settings with a default, and a failed hash degrades to the other two
sources with a visible health message rather than failing the import.

Owner's situation: an old account. Three outcomes are possible and the plugin has to say which one it is in:
account still works (library imports), npsso rejected (needs re-login), or account closed/region-locked. The
first build step is a probe with the owner's credentials; if it does not work, this plugin is parked and the
design stays.

Risks: against Sony's terms, account action is possible though unlikely for read-only calls. Stated to the owner
before they enter anything.

Ids: `psn:<npCommunicationId or titleId>`; `PS5`/`PS4` as platform; PS Plus catalog titles flagged
`OwnedViaSubscription` only if the API says so (unverified), otherwise not guessed.

### 5.3 Xbox (`Chronicle.Plugin.Xbox`) — unofficial but Microsoft-documented in part

What "Game Pass library" means: Game Pass is a subscription. The Xbox services expose **title history** (games the
account has played or has achievements in), not purchases and not "everything included in my subscription". The
Game Pass catalog is public (measured, 11 KB of product ids) but says what is available to anyone, not what the
owner has. So the achievable import is "games this Microsoft account has played on Xbox or Windows, with
last-played and achievement progress". Owned-never-played games are not reachable through title history, and the
purchase-history route needs a different, harder authentication. The design states this plainly so it is not a
surprise. A game never launched is not in the import; that is a documented limit, not a bug.

Do they have it? A Microsoft account alone is not enough: an Xbox profile (gamertag) must exist. If the owner
has never signed in to Xbox services the title history is empty. The first step is a probe, same as PSN.

Auth (from the OpenXbox client's docs, unverified live): register an application in Azure for personal Microsoft
accounts, OAuth in a browser, then the Xbox user token and an XSTS token (`Authorization: XBL3.0 x=<uhs>;<xsts>`).
The Azure registration is an owner setup step, like the Twitch one for IGDB. The refresh token expires if unused,
so a lapse means signing in again. Child accounts cannot be used. Title history is on `titlehub.xboxlive.com`
with a contract-version header; exact decoration values to be fixed during the probe.

Everything else follows Steam: one limiter, a breaker, an auth latch, raw record kept, nothing deleted when absent.

### 5.4 Nintendo (`Chronicle.Plugin.Nintendo`) — recommend: do not build

The owner has no Nintendo account, so there is nothing to test against. Independent of that:

- Nintendo has no public API and no library or purchase-history endpoint.
- The Switch Online app API does not expose a play log; the maintainer of the main community client says the only
  way to get one is from the console.
- The one route that returns play time (Parental Controls usage records) requires a console linked in Nintendo's
  parental-controls app, which the community client does not support adding, and per-title data is limited.
- The sign-in depends on reverse-engineered client attestation that the same project says may break, and it
  documents that Nintendo banned a small number of users for third-party SplatNet use.

Decision D2: Nintendo is documented here and **not built**. If the owner ever has an account, revisit with a probe
first. Switch games can still be added by hand with Add Media, which the IGDB plugin serves.

## 6. Matching and de-duplication

- One item per game. A game on Steam and PS5 is one item with two ownership rows. Matching order is the host's:
  own id, `AdditionalIds`, then title and year. Title and year is what joins a Steam item and a PSN item (they share
  no id), so normalisation matters: `™`, `®`, "GOTY", "Game of the Year Edition", platform suffixes
  ("(PS5)", "PS4 & PS5") and Roman numerals must fold the way IGDB's `TextRules` already do. That code is in
  another repository and plugins stand alone, so each plugin carries a small copy. Risk R1: two copies drift. Accepted;
  they are pinned by tests that share a fixture.
- Cross-edition risk: "Persona 5" and "Persona 5 Royal" are different games and must not merge. The IGDB
  plugin already learned this (edition words versus never-strip words); the rule is copied with its tests.
- Ambiguity never merges: two candidates for one title become two items and the duplicate scanner
  (`DuplicateCandidateScanService`) can offer the merge. Wrong merges are worse than duplicates.
- `year` is the release year from the platform; a remaster released a decade later has its own year and its own
  platform record, which separates it.

## 7. Resilience (applies to all three)

Taken from the IGDB design, which the owner approved:

| Event | Behaviour |
|---|---|
| HTTP 429, or no response | `HttpRequestException` with the status: the host pauses the pass and burns no retry |
| 401/403 on credentials | `PluginAuthException`, one latch, cleared when settings change, health shows the reason |
| 5xx | breaker opens 15 s, doubling |
| Platform returns an empty list | never treated as deletion; a drop of more than half versus last run is reported, not applied |
| Unknown field or shape change | tolerant JSON readers; the whole record is kept raw so nothing is lost |
| A title that cannot be matched | imported as its own item from platform data (still complete), never dropped |
| Token refresh | single-flight, immutable token record, refreshed tokens returned via `GetRefreshedSettings` so they survive restart |
| Secrets | password-type settings, redacted in logs and exceptions, never in query strings except where the platform requires it (Steam's `key=`, redacted) |

Timeouts: 25 s per call from `ProviderCallGuard`; library calls are single large responses, so the guard's two
retries apply. Import runs as a scheduled task, not an interactive call, so the 4 s search budget does not apply.

## 8. The type

Items are `game` (display "Video Games"), declared identically to IMDb and IGDB so the merge-by-name logic in
`SyncMediaTypesFromPluginsAsync` keeps one type. Each import plugin declares it the same way, so it exists
whichever is installed first, and the Library shows the Game section as soon as the first item lands. (It is empty
today only because there are zero games.)

## 9. What the owner sees

- Plugins page: one card per platform with its settings, a Test button that makes one safe call, and plain
  messages: "profile is private", "key rejected", "npsso expired, paste a new one", "no Xbox profile on this
  account".
- Library: Video Games, one item per game, status Unwatched/Watching, hours played, last played, platforms owned.
- The item page shows the ownership rows (platform, hours, last played, via subscription) and achievements.

## 10. Phasing

1. Host change (section 4) and Steam. Fully testable today with the owner's key. Includes the probe step and recorded
   fixtures, exactly as IGDB did.
2. Library UI for playtime and platforms.
3. PlayStation, after a probe with the owner's old account says whether it is worth it.
4. Xbox, after a probe says whether the account has title history.
5. Nintendo: not built.

## 11. Open decisions

| # | Decision | Recommendation |
|---|---|---|
| D1 | Ownership as a new table or columns on `UserLibrary` | New table |
| D2 | Nintendo | Do not build |
| D3 | Owned-never-played games map to `Unwatched` | Yes |
| D4 | Steam non-game apps (soundtracks, demos, DLC the owner owns) | Import, tagged by type (lossless rule) |
| D5 | Steam achievements as a separate weekly task | Yes, off by default |
| D6 | PSN and Xbox are built only after a live probe passes | Yes |
| D7 | Shared text-normalising code copied per plugin versus a shared library | Copy, pinned by shared fixtures |

## 12. Review of this design (first pass)

Done against the Chronicle source and the IGDB plugin's lessons.

1. **Fake plays.** An owned game must not become an `InteractionEvent` or the history fills with plays that never
   happened. Fixed: section 4 stores ownership separately and only creates an event for a genuinely newer
   last-played time.
2. **`Completed` on any play** is the host's rule for movies and is wrong for a game with 3 minutes played. Fixed:
   section 4 uses `Watching`; `Completed` only from all achievements or the user.
3. **Empty response means deleted** would wipe a library when a profile turns private. Fixed: nothing is removed
   for absence; large drops are reported.
4. **A bad Steam key looks like no key** (both 401). The latch must not retry. Covered in 5.1 and 7.
5. **The old `PLUGIN_STEAM.md` is wrong** about `GetAppList` (404 today). Fixed: this document supersedes it.
   It also puts the interface in a "metadata plugin"; the library import is a separate capability, so it stays
   in one plugin class implementing both `IImportProvider` and `IMetadataProvider`.
6. **Xbox expectations.** "Game Pass library" is not obtainable as the owner imagines. Stated as a limit in
   5.3 and in the UI text, so it is not mistaken for a failure.
7. **PSN persisted-query hash rotates.** Degrade, do not fail (5.2).
8. **Cross-platform merge risk** (Persona 5 vs Royal). Covered in section 6 with ambiguity-never-merges.
9. **Standalone rule**: no section above requires IGDB. The `steam:` additional id is an id IGDB reads, not a
   dependency; the plugin works if IGDB is absent.
10. **Not yet reviewed:** the shape of the Steam owned-games response and the private-profile behaviour are
    unverified; the PSN and Xbox endpoints are from community documentation. The probe steps exist for this.

## 13. Sources

Steam partner docs (IPlayerService), live probes in section 3, the `psn-api` community library README and function
list, the OpenXbox `xbox-webapi-python` documentation, Microsoft's Xbox Live REST reference for title history, and
the `nxapi` project README. Community libraries are unofficial and not affiliated with the platforms.

## 14. What the build needs from the owner

- **Steam:** a Web API key from steamcommunity.com/dev/apikey (the form asks for a domain name; what it accepts is unverified),
  and the SteamID64 or vanity name. The Steam profile's "Game details" must be public, at least during import.
- **PlayStation (later):** whether the account still signs in at playstation.com; if so, the npsso value at
  probe time.
- **Xbox (later):** whether a Microsoft account with an Xbox profile exists; if so, an Azure app registration.
- **Nintendo:** nothing.
