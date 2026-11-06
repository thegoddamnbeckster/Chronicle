# Steam plugin

**Version:** 1.1.2  ·  **Repository:** `Chronicle.Plugin.Steam`  ·  **Media types:** game

**What it needs from the owner:** A Steam Web API key and the owner's Steam ID; the profile's game details must be public.

**The source's terms and limits:** Steam Web API terms; the store is paced at about 200 requests per 5 minutes. (https://steamcommunity.com/dev)

The plugin's own README follows; the repository is the source of truth.

---

﻿# Chronicle.Plugin.Steam

Your Steam library in [Chronicle](https://github.com/thegoddamnbeckster/Chronicle), and Steam's store data for every game.

It does two jobs and works with no other plugin installed:

* **Library** (an import provider): every game and app you own with minutes played and last played, your wishlist, your
  Steam level and badges. A game you never started arrives as "owned, 0 minutes", never as a play.
* **Metadata** (a metadata provider): everything the Steam store knows about a game (description, genres, categories,
  developers, publishers, release date, screenshots, trailers, requirements, DLC, ratings) and your achievements for it.

Everything Steam sends is kept. Nothing is capped or filtered; the complete record for each game is stored whole.

## Setup

1. Get a free Web API key at <https://steamcommunity.com/dev/apikey> (the form asks for a domain name; `localhost` works).
2. In Steam, open Edit Profile, then Privacy Settings, and set **Game details** to **Public** (at least while Chronicle imports).
   Steam lists a library only for a public profile; a private one answers with an empty list, and the plugin says so rather than
   reporting an empty library.
3. In Chronicle, open Plugins, Steam, Settings. Paste the key and your Steam account. Open your profile in a browser: if its address is
   `https://steamcommunity.com/profiles/76561198012345678` (an example), enter the number at the end; if it is `https://steamcommunity.com/id/yourname`, enter the name at the end. `https://steamcommunity.com/my/profile` takes you to your own profile when you are signed in.
   The name shown at the top of the profile is a display name and will not work. Save, then Test.
4. Run **Import Everything from Steam** once. After that **Sync Steam Library** runs daily.

The key is saved encrypted, never logged, and sent only to Steam.

## What happens to your library

* One Chronicle item per game; a game you also own on another platform is the same item with a second ownership row.
* New items start as **Not started** (**Planned** for a wishlist game, **In progress** when you have hours). Chronicle never
  changes a status you set yourself, and a game you delete from Chronicle is not brought back by the next sync.
* Hours played and last played update in place. A play is recorded only when "last played" moves forward.

## Limits worth knowing

* Steam's store allows about 200 requests per 5 minutes, so naming a large wishlist and fetching store data for a large library
  takes a while the first time. The plugin paces itself and pauses on a rate limit without failing anything.
* Achievements are fetched per game, for games that have them. A game you do not own, or one with private stats, simply has none.
* The store knows nothing about delisted games; they keep their name and playtime from your library.

## Develop

```
dotnet test tests/Chronicle.Plugin.Steam.Tests
```

Fixtures under `tests/fixtures` are real Steam responses recorded on 2026-11-05 with the key and account id removed. A live check
against your own account (read-only) runs only when `STEAM_LIVE=1`, `STEAM_KEY` and `STEAM_ID` are set.

Design: [docs/plans/2026-11-05-game-library-sync-design.md](https://github.com/thegoddamnbeckster/Chronicle/blob/main/docs/plans/2026-11-05-game-library-sync-design.md).

## License

MIT
