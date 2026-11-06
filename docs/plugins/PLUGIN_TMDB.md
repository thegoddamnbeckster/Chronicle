# TMDB plugin

**Version:** 1.9.1  ·  **Repository:** `Chronicle.Plugin.TMDB`  ·  **Media types:** n/a

**What it needs from the owner:** A TMDB API key.

**The source's terms and limits:** Free for non-commercial use; TMDB requires attribution and its logo where its data is shown. (https://www.themoviedb.org/api-terms-of-use)

The plugin's own README follows; the repository is the source of truth.

---

# Chronicle.Plugin.TMDB

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.TMDB?label=Chronicle.Plugin.TMDB&color=01b4e4)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB/releases/latest)

TMDB metadata provider plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Fetches movie and TV metadata — titles, overviews, cast, crew, ratings, genres, poster and backdrop images — from [The Movie Database (TMDB)](https://www.themoviedb.org/).

## Supported Media Types

| Media Type | Fields |
|------------|--------|
| `movie`    | title, overview, year, poster_url, backdrop_url, runtime_minutes, genres, cast, directors, rating |
| `tv`       | title, overview, year, poster_url, backdrop_url, genres, cast, rating |
| `anime`    | title, overview, year, poster_url, backdrop_url, genres, cast, rating |
| `fanedits`  | title, overview, year, poster_url, backdrop_url, runtime_minutes, genres, cast, directors, rating |
| `tv.1` (Seasons) | title, overview, year, poster_url, backdrop_url |
| `tv.2` (Episodes) | title, overview, year, poster_url, runtime_minutes |
| `people`   | title, overview, poster_url, birth_date, death_date (contributes to an existing person only — cross-referenced by ID, never a blind name search) |

Movie collections are also supported via `GetByIdAsync` (looked up by TMDB collection id,
never by search — TMDB has no search-by-collection endpoint): poster, backdrop, and logo
are all resolved through the same language-preference-aware image selection, from
`/collection/{id}/images`'s full, unfiltered set of alternates.

## External ID Format

`{type}:{tmdbId}` — for example:

- `movie:550` → Fight Club
- `tv:1399` → Game of Thrones
- `tv:94997` → Shōgun (also returned for anime mapped to TMDB TV)

Fix Match accepts full TMDB URLs:
- `https://www.themoviedb.org/movie/550`
- `https://www.themoviedb.org/tv/1399/season/2/episode/5`

## Installation

1. Build the plugin:
   ```powershell
   dotnet build -c Release
   ```

2. Copy `bin\Release\net9.0\*.dll` and `manifest.json` into your Chronicle `plugins\chronicle.plugin.tmdb\` directory.

3. After installation, go to Chronicle → Plugins → TMDB → Settings and enter your API key.

## Configuration

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `api_key` | ✓ | — | TMDB v3 API key. Free at https://www.themoviedb.org/settings/api |
| `language` | | `en-US` | BCP 47 language tag for titles and overviews |
| `include_adult` | | `false` | Include adult content in search results |
| `poster_size` | | `w500` | TMDB image size for poster art |
| `backdrop_size` | | `w1280` | TMDB image size for backdrop images |

## Deploying

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.tmdb"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\*.dll" $pluginDir
Copy-Item "manifest.json"           $pluginDir
```

## Development

Both repositories must be cloned as siblings for the project reference to resolve:

```
<base>\
  Chronicle\
  Chronicle.Plugin.TMDB\
```

The plugin references `Chronicle.Plugins` via a local project reference:

```xml
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
```

> **Important:** `Chronicle.Plugins.dll` must **not** be in the plugin output directory — the Chronicle host provides it. `<Private>false</Private>` prevents it from being copied.
