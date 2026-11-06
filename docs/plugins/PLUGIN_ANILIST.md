# AniList plugin

**Version:** 1.0.5  ·  **Repository:** `Chronicle.Plugin.AniList`  ·  **Media types:** anime, anime_movies

**What it needs from the owner:** None. Public GraphQL API.

**The source's terms and limits:** Free for non-commercial use; be polite with the request rate. (https://anilist.co)

The plugin's own README follows; the repository is the source of truth.

---

# Chronicle.Plugin.AniList

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.AniList?label=Chronicle.Plugin.AniList&color=02a9ff)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.AniList/releases/latest)

Anime metadata plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Fetches anime metadata — titles in English, romaji and native, synopses, genres, tags, studios, voice cast, staff, ratings, posters, banners, seasons, and episode titles — from [AniList](https://anilist.co/) via its public GraphQL API.

**Plugin ID:** `chronicle.plugin.anilist`
**Version:** 1.0.4
**Auth:** None (no API key or account required)

---

## Supported Media Types

| Media Type | Shape | Matches | Fields |
|------------|-------|---------|--------|
| `anime` | Show / Season / Episode | AniList entries of format TV, TV short, ONA and OVA | title, overview, year, poster_url, banner_url, genres, tags, cast (voice actors), crew (staff, animation studio), rating; episodes: title, thumbnail, air date |
| `anime_movies` | one flat item | AniList entries of format MOVIE, OVA and special (feature-length OVAs are common) | title, overview, year, poster_url, banner_url, runtime, genres, tags, cast, crew, rating |

Music videos are not matched. A regular TV series or feature film is preferred over an OVA or special of the same name.

---

## External ID Format

Stored under source `anilist`:

| ID | Meaning |
|----|---------|
| `show:{id}` | A show (the first season's AniList id) |
| `show:{id}/season:{n}` | A season |
| `show:{id}/season:{n}/episode:{e}` | An episode, numbered across the whole season |
| `movie:{id}` | A film |

**Fix match** accepts any of: an AniList id (`16498`), an AniList URL, `mal:16498`, or a MyAnimeList URL.

The MyAnimeList id is published as a cross-reference (`ids.mal`) for other plugins to use.

---

## Features

### How seasons work

AniList has no "series with seasons": every season is its own entry, linked to its neighbours by *prequel* and *sequel* relations. The plugin turns that back into Show / Season / Episode:

- The **show** is the first season's entry. A search that lands on a later season ("Attack on Titan Season 2") is followed back to the first.
- Each following TV-like **sequel** is the next season. Movies, OVAs and specials in between are skipped, and a link to an entry that started *before* its "sequel" (or after its "prequel") is ignored, since AniList's relations are community-maintained and occasionally loose.
- A sequel whose title says it is a continuation ("Part 2", "2nd Cour", "Final Chapters") is **folded into the season before it**, and episodes keep counting up, so a split season of 12 + 10 episodes is one season of 22.

When your numbering differs from AniList's, use **Fix match** on the season, or rely on another TV provider for episode numbering.

### Episode details

AniList does not have a full episode guide. Episode titles and thumbnails come from the streaming listings it carries, and air dates from its airing schedule, which exists mostly for shows from the last decade. An episode with no title is still listed as "Episode N"; one with no recorded air date simply has none.

### Rate limiting

AniList allows 30 requests a minute (it reports the limit in every answer, and the plugin follows it), so requests are spaced just over two seconds apart. Chronicle gives a plugin 25 seconds per call, so a rate-limit answer is not waited out: a short wait is retried once, anything longer is reported as an HTTP 429, which Chronicle treats as "provider rate limited" and leaves the item **Pending** for the next run, not "not found". Entries just fetched are remembered for ten minutes while a season chain is walked.

A first run over a large anime library is therefore slow by design: expect roughly 6 to 10 seconds per show and 4 to 6 per film.

---

## Setup

No account or API key is needed.

1. In Chronicle → **Plugins**, install this plugin from the catalog.
2. Enable it. Use **Health check** to confirm Chronicle can reach AniList.

---

## Configuration

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `title_language` | | `english` | Which title is the item's name: `english` (falls back to romaji), `romaji` or `native`. The others are kept as alternate names |
| `include_adult` | | `false` | Allow entries AniList marks adult to be matched |

---

## Background Tasks

| Task | Default schedule | Description |
|------|------------------|-------------|
| Fetch Missing Anime Metadata | Daily 04:00, on | Looks up AniList metadata for anime and anime movies not yet enriched by AniList |
| Re-sync All Anime Metadata | Weekly Sunday 02:00, off | Re-fetches everything to pick up new episodes, season changes, scores and artwork |

---

## Repository Structure

```
Chronicle.Plugin.AniList/
├── Chronicle.Plugin.AniList.csproj
├── manifest.json
├── AniListMetadataProvider.cs   # IMetadataProvider implementation
├── AniListSeasons.cs            # prequel/sequel chain -> Show / Season layout
├── AniListClient.cs             # rate-limited GraphQL client
├── AniListModels.cs             # GraphQL response models
└── tests/Chronicle.Plugin.AniList.Tests/
```

---

## Building

```powershell
dotnet build -c Release
```

Copy output to your Chronicle plugin directory:

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.anilist"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.AniList.dll"       $pluginDir
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.AniList.deps.json" $pluginDir
Copy-Item "manifest.json"                                         $pluginDir
```

> **Important:** `Chronicle.Plugins.dll` must **not** be in the plugin directory — Chronicle provides it. The `.csproj` sets `Private="false"` on the Chronicle.Plugins reference to ensure this.

Tests:

```powershell
dotnet test tests/Chronicle.Plugin.AniList.Tests                            # canned data, no network
$env:ANILIST_LIVE = "1"; dotnet test tests/Chronicle.Plugin.AniList.Tests   # also the real AniList
```

---

## Development

The plugin references `Chronicle.Plugins` via a local project reference:

```xml
<ProjectReference Include="../Chronicle/src/Chronicle.Plugins/Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
```

Both repositories must be cloned as siblings for this to resolve:

```
<base>\
  Chronicle\
  Chronicle.Plugin.AniList\
```

---

## License

MIT
