# Chronicle.Plugin.Steam

**Plugin ID:** `chronicle.plugin.steam` | **Version:** 1.0.0 | **Type:** `game` | **Auth:** Steam Web API key (free) and a public profile

Implemented 2026-11-05. The first draft of this page named `ISteamApps/GetAppList/v2`, which no longer exists (404); the endpoints below were
measured against a real account. Full design, measurements and review: [docs/plans/2026-11-05-game-library-sync-design.md](../plans/2026-11-05-game-library-sync-design.md).
New-plugin checklist: [docs/PLUGIN_NEW_TYPE_CHECKLIST.md](../PLUGIN_NEW_TYPE_CHECKLIST.md).

## What it does

| Role | Source | Notes |
|---|---|---|
| Library import | `IPlayerService/GetOwnedGames` (appinfo, free games, extended appinfo) | one response, no paging; 316 games measured, 226 never played |
| Wishlist | `IWishlistService/GetWishlist` (key-less) | appid, priority, date added; names from the store |
| Account | `GetPlayerSummaries`, `GetSteamLevel`, `GetBadges`, `GetRecentlyPlayedGames` | kept whole under the account's settings key |
| Store metadata | `store.steampowered.com/api/appdetails` | the whole record is kept; about 200 requests per 5 minutes |
| Achievements | `GetSchemaForGame`, `GetPlayerAchievements`, `GetGlobalAchievementPercentagesForApp` | per game; a game not owned answers 403 with JSON, which is an absence, not a bad key |

## Behaviour that was measured, not assumed

* A wrong or missing key answers 401 or 403 with an **HTML** page ("Access is denied. Retrying will not help"); asking for achievements of a game the
  account does not own answers 403 with **JSON**. Only the first latches the key as rejected.
* A private "Game details" setting answers an empty `response`, which is reported as private, never as an empty library.
* `rtime_last_played` of 0 means never played, and is never turned into a date.
* Art: `library_600x900.jpg` (poster) and `header.jpg` exist on Steam's CDN for every app checked.

## Tasks

`delta-sync` (daily library import), `import-all` (manual full import), `fetch-missing-metadata` (store data and achievements).
