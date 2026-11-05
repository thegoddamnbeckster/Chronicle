# Chronicle.Plugin.IGDB

**Plugin ID:** `chronicle.plugin.igdb`
**Version:** 1.0.0 (built 2026-11-05, not yet released)
**Media Types:** Video Games (`game`), one level
**Auth:** Twitch client ID + client secret (free at dev.twitch.tv; the Twitch account needs two-factor authentication)
**API:** IGDB API v4, `https://api.igdb.com/v4`; tokens from `https://id.twitch.tv/oauth2/token`

The first and primary source for Chronicle's games library. It keeps **everything IGDB has for a game** (no caps, no filtering), matches by exact title or alternative name before IGDB's fuzzy search, and tells Chronicle about each kind of failure in the way that does the least harm (an outage or rate limit pauses the queue; it never burns an item's retries).

This page replaces an earlier scaffold-era design that predated the plugin API and listed `POST /games search "{title}"` as the whole algorithm.

## Where the real documentation is

* **Design, measurements and decisions:** [`docs/plans/2026-11-05-igdb-plugin-design.md`](../plans/2026-11-05-igdb-plugin-design.md). It records what the live service was measured to do (ranking, rate limit, error bodies, latency, payload sizes), what the host does with each exception the plugin can throw, an independent review, and section 21 on what building it changed (notably the type name).
* **Using it:** the plugin's own README in the `Chronicle.Plugin.IGDB` repository (setup, settings, id forms, how a game is matched, what a failure does).
* **Recorded responses and the probe scripts** that measured the service: `tests/fixtures/live-2026-11-05/` and `tools/` in that repository.

## The facts that shape it

| | |
|---|---|
| Type | `game`, declared **identically to the IMDb plugin** ("Video Games", played, percent): Chronicle merges the declarations of every plugin that names a type, so they must agree. Also read as `games`, `videogame`, `video_game` from clients. |
| Rate limit | 4 requests a second, 8 open (documented). A 150-request burst drew 429s with no `Retry-After`; IGDB answered again 0.6 s later. The plugin paces at 3 a second and backs off 2 s, doubling to 5 minutes. |
| Search | IGDB's fuzzy `search` put the exact title first for 22 of 43 titles; an exact-name-or-alternative-name query fixes most misses, so it goes first. |
| One game | `fields *` plus every expansion, its time to beat and its characters, in one multiquery: about 0.2 s, 30 to 120 KB. |
| Ids | The bare IGDB game id. Fix match also takes an igdb.com game page address and `steam:<appid>`. No cross-reference ids are published. |
| Not found | Permanent in Chronicle until a person resets it, so the plugin returns "not found" only for a genuine empty answer, never for a failure. |

## Not covered

Importing play history (IGDB has no accounts: a Steam/GOG plugin does that), scanning game files (no scanner understands games; do not make a scan folder of type Video Games), and grouping a franchise into a Chronicle collection (collections are movie-only in the host; the series is a tag).
