# Movies Remastered (MRDb) plugin

**Version:** 1.1.0  ·  **Repository:** `Chronicle.Plugin.MoviesRemastered`  ·  **Media types:** n/a

**What it needs from the owner:** None. Reads the public pages of moviesremastered.com.

**The source's terms and limits:** A scraped site: the plugin paces itself and identifies itself; be gentle. (https://moviesremastered.com)

The plugin's own README follows; the repository is the source of truth.

---

# Chronicle.Plugin.MoviesRemastered

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.MoviesRemastered?label=Chronicle.Plugin.MoviesRemastered&color=e60000)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.MoviesRemastered/releases/latest)

Metadata source plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) that
fetches fan edit metadata from [Movies Remastered (MRDb)](https://www.moviesremastered.com/),
a community fanedit database. Second metadata source for the `fanedits` media type, alongside
[Chronicle.Plugin.FanEdit](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FanEdit).

**Plugin ID:** `chronicle.plugin.moviesremastered`
**Version:** 1.0.0
**Media Types:** Fan Edits (`fanedits`)
**Auth:** None — search and detail pages are public
**Data source:** moviesremastered.com – HTML scraping (no public API)

---

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Rate Limiting Strategy](#rate-limiting-strategy)
- [Data Model](#data-model)
- [Scraping Approach](#scraping-approach)
- [Settings Schema](#settings-schema)
- [manifest.json](#manifestjson)
- [Background Tasks](#background-tasks)
- [Error Handling](#error-handling)
- [Repository Structure](#repository-structure)
- [Building & Packaging](#building--packaging)
- [Branding Reference](#branding-reference)
- [Important Notes on Use](#important-notes-on-use)

---

## Overview

[Movies Remastered](https://www.moviesremastered.com) (MRDb — the Movies Remastered
Database) is a community-run catalogue of fan-edited versions of movies and TV shows —
extended cuts, TV-to-movie edits, colour grades, and entirely custom re-edits. Each MRDb
entry describes the edit itself, its relationship to the source material, the faneditor who
created it, technical specs, and community reception.

Unlike fanedit.org, MRDb requires **no account** to browse — search results and full detail
pages (synopsis, change list, ratings) are public. This plugin makes no authenticated
requests at all.

MRDb is a small, community-maintained site. It does not publish a stated rate limit the way
fanedit.org does, but this plugin applies the same courtesy throttle regardless: all HTTP
requests are limited to a minimum of one second apart, and the plugin is intended for
personal, single-user use.

---

## Architecture

This plugin implements `IMetadataProvider` from `Chronicle.Plugins`. It is a **scraping
provider** — there is no public API, so all data is extracted from the HTML returned by the
site's pages.

```
Chronicle Host
    │
    ├── Configure(settings)        ← request delay / user-agent stored
    │
    ├── HealthCheckAsync()         ← GET /hub.php; return true on 2xx
    │
    ├── SearchAsync(context)       ← GET /searchresults.php?searchtype=Title&searchterm=...
    │       └─ returns scored candidates with ExternalId = "mrdb:{id}"
    │
    ├── GetByIdAsync(externalId)   ← GET /movieinfo.php?id={id}; parse full record
    │       └─ returns MediaMetadata with all fields + ExtendedData
    │
    └── GetImageAsync(url)         ← Chronicle calls this if it needs raw image bytes
            └─ GET {image-url} directly (respects rate limit)
```

### Stateless Design

In keeping with Chronicle's plugin contract, `MoviesRemasteredMetadataProvider` is
**stateless between `Configure` calls** — no session state to manage since there's no login.

### Dependency Overview

| Dependency | Purpose |
|---|---|
| `HtmlAgilityPack` | HTML parsing / XPath queries against page DOM |
| `System.Net.Http.HttpClient` | HTTP transport |
| No additional NuGet packages required | MRDb returns plain HTML |

### Why a second provider for the same media type works

Chronicle's plugin architecture is additive, not exclusive: multiple `IMetadataProvider`s can
declare the same media type simultaneously. Each gets its own enrichment row per item, and
the Enrichment Status table shows one row per installed plugin. When both
Chronicle.Plugin.FanEdit and this plugin are installed, conflicting field values are
reconciled by the user via **Settings → Metadata Assignment** — a generic, per-field
priority list. No plugin-specific merge logic exists or is needed.

---

## Rate Limiting Strategy

MRDb doesn't publish a stated rate limit, but this plugin applies the same courtesy floor as
Chronicle.Plugin.FanEdit out of respect for volunteer-run infrastructure.

### Rules

| Rule | Value | Rationale |
|---|---|---|
| Minimum inter-request gap | **1,000 ms** (configurable, floor enforced) | At most 1 request/second |
| Minimum floor (absolute) | **1,000 ms** | Hard-coded lower bound; cannot be set lower |
| Implementation | `SemaphoreSlim(1,1)` + `Stopwatch` elapsed check | Serialises all requests |
| Applies to | ALL requests (search, detail, image, health check) | No request is exempt |

### Implementation Pattern

```csharp
private readonly SemaphoreSlim _gate = new(1, 1);
private readonly Stopwatch _last = Stopwatch.StartNew();

public async Task ThrottleAsync(CancellationToken ct)
{
    await _gate.WaitAsync(ct);
    try
    {
        var elapsed = _last.ElapsedMilliseconds;
        if (elapsed < DelayMs)
            await Task.Delay((int)(DelayMs - elapsed), ct);
        _last.Restart();
    }
    finally { _gate.Release(); }
}
```

---

## Data Model

### ExternalId Format

```
mrdb:{id}
```

Where `{id}` is the numeric MRDb ID visible in the entry URL, e.g. `mrdb:12179`. Unlike
fanedit.org, MRDb has no slug-based URLs — IDs are purely numeric.

This value is stored in `media_external_ids` with `Source = "moviesremastered"`.

### MediaMetadata Mapping

| MRDb field | `MediaMetadata` property | Notes |
|---|---|---|
| Fanedit title | `Title` | JSON-LD `name`, stripped of site-name suffix |
| Synopsis | `Overview` | Full text from the `<H3>Synopsis:</H3>` section |
| Fanedit release year | `Year` | Parsed from Fanedit Release Date |
| Fanedit runtime | `RuntimeMinutes` | Detail page uses `{h}h:{m}m:{s}s`; converted to minutes |
| Cover image | `PosterUrl` | From `og:image` — stable, no cache-bust query string |
| Genre | `Genres` | Multi-value, `•`-separated on the page |
| MRDb Rating | `Rating` | 0–10; `null` when the page shows "No votes" |
| Franchise, Fanedit Type | `Tags` | |
| Everything else | `ExtendedData` | See below |

### ExtendedData Schema

All MRDb-specific metadata with no generic `MediaMetadata` counterpart is stored in
`ExtendedData` as a `JsonElement` — nothing is discarded.

```jsonc
{
  "originalTitle":          "Game of Thrones (TV Series)(2011)",
  "originalReleaseDate":    "3rd January 2011",
  "originalRuntimeMinutes": 541,

  "faneditorUsername":   "Spartan47",
  "faneditorProfileUrl": "Spartan47",
  "fanEditType":         "TV-to-Movie",
  "franchise":           "Game of Thrones",

  "techSpecs": {
    "source":     "4K",
    "resolution": "4k",
    "soundMix":   "5.1. Channels"
  },

  "certificate": "18",
  "language":    "English",
  "subtitles":   ["English", "Spanish"],

  "timeCut":      "5h:23m:0s",
  "timeAdded":    "0h:0m:0s",
  "intentions":   "To combine Jon and Bran's storylines from season 1 and 2 into a single, more focused narrative.",
  "changeList":   "Combined Jon Snow's storyline with the key parts of Bran Stark's journey. ...",

  "views":         95,
  "reviewCount":   0,
  "favoriteCount": 0,

  "mrdbId":      "12179",
  "mrdbUrl":     "https://www.moviesremastered.com/movieinfo.php?id=12179",
  "releaseDate": "25th July 2026"
}
```

### Image Handling

Images are **stored as URLs only** — never downloaded or re-hosted.

| Image role | Chronicle field | Notes |
|---|---|---|
| Primary cover / poster | `MediaMetadata.PosterUrl` | `og:image` — cache-param-free |

---

## Scraping Approach

MRDb's HTML is not semantically classed (unlike fanedit.org's JReviews `jrFieldRow`/
`jrFieldLabel`/`jrFieldValue` structure) — fields are a flat run of `<B>Label:</B>` value
`<BR>` siblings. The scraper walks child nodes, tracking the current label and buffering
values until the next label or `<HR>`.

#### Search

```
GET https://www.moviesremastered.com/searchresults.php?searchtype=Title&genre=&franchise=&certificate=&award=&language=&fanedittype=&searchterm={query}
```

A real title-search endpoint — no slug-guessing needed (unlike FanEdit). For each result,
extract: title, MRDb ID, original title, faneditor, franchise, fanedit type, release year,
runtime (already in minutes here), synopsis, rating. Scored using Levenshtein distance +
year proximity.

#### Detail Page

```
GET https://www.moviesremastered.com/movieinfo.php?id={id}
```

Extraction priority (highest to lowest confidence):

1. **JSON-LD** (`@type: "Movie"`) — `name`, `description`, `image`
2. **OpenGraph meta tags** — `og:title`, `og:description`, `og:image` (fallback)
3. **Flat `<B>Label:</B>` field walk** — Faneditor, Fanedit Type, Franchise, Genre, Original
   Title, Certificate, Source, Resolution, Sound Mix, Language, Subtitles, etc.
4. **`<H3>` free-text sections** — Synopsis, Intentions, Change List
5. **`div.stats-item` blocks** — MRDb Rating, Views, Reviews, Favorites

#### Fix Match Input Handling

- Full moviesremastered.com URL → call `GetByIdAsync` directly
- Bare numeric ID → call `GetByIdAsync` directly
- Otherwise → title search query

---

## Settings Schema

| Key | Label | Type | Required | Default | Notes |
|---|---|---|---|---|---|
| `request_delay_ms` | Request Delay (ms) | Number | No | `1000` | Floor: 1000. Be kind to the server. |
| `user_agent` | User-Agent String | Text | No | See below | Override HTTP User-Agent |

No credentials of any kind — MRDb requires no account.

**Default User-Agent:**
```
Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36
```

---

## manifest.json

```json
{
  "plugin_id":             "chronicle.plugin.moviesremastered",
  "name":                  "Movies Remastered (MRDb)",
  "version":               "1.0.0",
  "author":                "Chronicle Contributors",
  "description":           "Fetches fan edit metadata from the Movies Remastered Database (moviesremastered.com / MRDb), a community fanedit archive. No account required. Please use responsibly — a minimum 1-second delay between requests is enforced.",
  "min_chronicle_version": "0.1.0",
  "entry_type":            "Chronicle.Plugin.MoviesRemastered.MoviesRemasteredMetadataProvider",
  "iconUrl":               "data:image/svg+xml;base64,...",
  "brandColorLight":       "#CC0000",
  "brandColorDark":        "#FF0000",
  "fixMatchHint":          "Enter a moviesremastered.com URL (e.g. https://www.moviesremastered.com/movieinfo.php?id=12179) or a bare MRDb numeric ID",
  "background_tasks": [ /* see Background Tasks below */ ]
}
```

**On the icon:** MRDb's real `favicon.ico` returns HTTP 200 with `Content-Type:
image/x-icon`, but the actual bytes are a JPEG, not a valid ICO file — the same "wrong
content type from an external favicon" problem that led Chronicle.Plugin.FanEdit to embed
its icon inline. This plugin does the same: an inline base64 SVG film-strip icon in the
site's real accent colour (`#FF0000`, confirmed by sampling the actual MRDb logo image), so
there's no external dependency and nothing to break.

---

## Background Tasks

| Task | Schedule | Purpose |
|---|---|---|
| `fetch-missing-metadata` | Manual only, with confirmation | Enriches fan edits that don't have MRDb metadata yet |
| `resync-all-metadata` | Manual only, with confirmation | Re-fetches MRDb metadata for every fan edit in the library |

Both are intentionally `schedulable: false` and require confirmation before running, out of
respect for MRDb's volunteer-run infrastructure — no default cron, no unattended re-scraping
of the whole library.

---

## Error Handling

| Condition | Behaviour |
|---|---|
| HTTP 404 on detail page | `GetByIdAsync` throws `KeyNotFoundException` |
| HTML field not found | Field set to `null`, parsing continues |
| Network timeout | Default `HttpClient` timeout; exception propagates with context |
| `CancellationToken` cancelled | Propagate immediately |
| `GetByIdAsync`/`GetImageAsync` given a non-MRDb URL | Throws `ArgumentException` (SSRF guard) |

---

## Repository Structure

```
Chronicle.Plugin.MoviesRemastered/
├── Chronicle.Plugin.MoviesRemastered.csproj
├── README.md
├── LICENSE
├── manifest.json
├── MoviesRemasteredMetadataProvider.cs   # IMetadataProvider — search, get by ID, Fix Match
├── MoviesRemasteredScraper.cs            # HTML parsing (search results + detail pages)
├── MoviesRemasteredRateLimiter.cs        # Per-instance rate limiting (SemaphoreSlim + Stopwatch)
├── Models/                               # Data transfer objects for scraped data
└── tests/                                # xUnit + FluentAssertions test suite
```

---

## Building & Packaging

Both repositories must be cloned as siblings for the project reference to resolve:

```
<base>\
  Chronicle\
  Chronicle.Plugin.MoviesRemastered\
```

```powershell
dotnet build -c Release
dotnet test tests/
```

Deploy to a local Chronicle dev instance — either run
`Chronicle\scripts\RunTestEnvironment.ps1` (rebuilds and deploys every installed plugin
automatically), or deploy just this plugin:

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.moviesremastered"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.MoviesRemastered.dll" $pluginDir
Copy-Item "bin\Release\net9.0\HtmlAgilityPack.dll"                   $pluginDir
Copy-Item "manifest.json"                                             $pluginDir
```

Or hot-deploy to an already-running API with `Chronicle\scripts\Deploy-Plugin.ps1
chronicle.plugin.moviesremastered`.

> **Important:** `Chronicle.Plugins.dll` must **not** be in the plugin directory — Chronicle
> provides it. The `.csproj` sets `<Private>false</Private>` on the `Chronicle.Plugins`
> project reference to ensure this.

```xml
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
```

---

## Branding Reference

| Plugin | Light | Dark |
|---|---|---|
| TMDB | `#01B4E4` | `#0d9ec9` |
| MusicBrainz | `#BA478F` | `#CF6BAA` |
| FanEdit (IFDB) | `#8B1A1A` | `#C0392B` |
| **Movies Remastered (MRDb)** | **`#CC0000`** | **`#FF0000`** |

The dark-mode value is the site's actual accent red, confirmed by sampling the real MRDb
logo image and cross-checked against the `color:red` styling used on the site's own
Synopsis/Intentions/Change List section headers. The light-mode value is a darkened variant
of the same hue for readability on light backgrounds.

---

## Important Notes on Use

1. **Personal use only.** Not appropriate for multi-user deployments.
2. **Respect Movies Remastered.** Do not reduce the request delay below 1,000 ms or run
   resync more than a few times per week.
3. **No redistribution of scraped data.** Metadata is for personal reference only.
4. **No credentials involved** — nothing to secure, nothing to leak.

---

## License

MIT – see [LICENSE](LICENSE).

---

*Chronicle.Plugin.MoviesRemastered is an independent community plugin and is not
affiliated with, endorsed by, or officially supported by Movies Remastered or any of its
staff.*
