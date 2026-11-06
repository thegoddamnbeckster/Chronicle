# Fanart.tv plugin

**Version:** 1.1.3  ·  **Repository:** `Chronicle.Plugin.FanartTV`  ·  **Media types:** n/a

**What it needs from the owner:** A Fanart.tv API key; an optional personal client key raises the limits.

**The source's terms and limits:** Artwork belongs to its uploaders; the project asks for attribution. (https://fanart.tv)

The plugin's own README follows; the repository is the source of truth.

---

# Chronicle.Plugin.FanartTV

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.FanartTV?style=flat-square&label=release&color=19b7e4)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FanartTV/releases/latest)

High-quality artwork plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) powered by [Fanart.tv](https://fanart.tv/).

Fetches posters, backdrops, HD clear logos, disc art, banners, clearart, and character art for movies, TV shows, anime, and music — community-sourced, high-resolution images that go beyond what TMDB and MusicBrainz provide.

---

## How It Works

Fanart.tv has no text-search endpoint. Rather than doing a title search, this plugin cross-references external IDs already stored on the item by other enrichment providers:

| Media Type | ID Source | Fanart.tv Endpoint |
|------------|-----------|-------------------|
| Movies, Fan Edits | TMDB `movie:{id}` | `/v3.2/movies/{tmdbId}` |
| TV, Anime | TVDB ID | `/v3.2/tv/{tvdbId}` |
| Music (Artist) | MusicBrainz artist MBID | `/v3.2/music/{mbid}` |

**This means TMDB must enrich movies/TV before Fanart.tv can run**, and Trakt/SIMKL sync (which stores TVDB IDs) must run before TV shows get Fanart.tv artwork.

Album cover art and CD art are stored per-album inside the artist response, keyed by MusicBrainz release-group MBID.

---

## Supported Media Types

| Media Type | Artwork Fields |
|------------|---------------|
| `movies`   | poster, backdrop, HD logo, banner, disc art, clearart, thumb |
| `fanedits`  | poster, backdrop, HD logo, banner, disc art, clearart, thumb |
| `tv`       | poster, backdrop, HD logo, banner, clearart, thumb, character art, season posters |
| `anime`    | poster, backdrop, HD logo, banner, clearart, thumb, character art, season posters |
| `music`    | artist thumb, backdrop, HD logo, banner, clearart; album cover + CD art per album |

Fields that don't have a first-class column in Chronicle's schema (logo_url, banner_url, disc_url, clearart_url, thumb_url, character_art_url, albums) are stored in `metadata_json` under the `chronicle.plugin.fanarttv` key.

---

## External ID Format

This plugin stores IDs in the following formats:

| Format | Example | Notes |
|--------|---------|-------|
| `movie:{tmdbId}` | `movie:550` | Fight Club |
| `tv:{tvdbId}` | `tv:76290` | Breaking Bad (TVDB ID) |
| `artist:{mbid}` | `artist:b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d` | The Beatles |

**Fix Match:** enter any of the above formats, or a Fanart.tv URL such as:
- `https://fanart.tv/movie/550/` 
- `https://fanart.tv/series/76290/`

---

## Image Selection

Images are ranked by:
1. **Language preference** — configured language first, then English, then language-neutral (`00`)
2. **Likes** — community up-votes descending within each language tier

HD variants (e.g. `hdmovielogo`, `hdclearart`) are preferred over SD variants when available.

---

## Installation

1. Build the plugin:
   ```powershell
   dotnet build -c Release
   ```

2. Copy `bin\Release\net9.0\*.dll` and `manifest.json` into your Chronicle `plugins\chronicle.plugin.fanarttv\` directory.

3. Go to Chronicle → Plugins → Fanart.tv → Settings and enter your API key.

---

## Configuration

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `api_key` | ✓ | — | Fanart.tv project API key. Free at https://fanart.tv/get-an-api-key/ |
| `client_key` | | — | Personal API key — unlocks images submitted in the last 7 days. Optional. |
| `language` | | `en` | ISO 639-1 language code for image language preference. |

---

## Dependencies

Fanart.tv artwork is a **supplementary layer** — it works best when other providers have already enriched the item:

- **Movies / Fan Edits** — requires TMDB plugin (stores TMDB movie ID)
- **TV / Anime** — requires Trakt or SIMKL sync (stores TVDB ID) or manual Fix Match
- **Music** — requires MusicBrainz plugin (stores MusicBrainz artist/album MBID)

Run the background enrichment tasks in this order for best results:
1. TMDB — Fetch Missing Metadata
2. Trakt/SIMKL — Import / Delta Sync (if applicable)
3. MusicBrainz — Fetch Missing Metadata
4. **Fanart.tv — Fetch Missing Artwork**

---

## Development

Both repositories must be cloned as siblings:

```
<base>\
  Chronicle\
  Chronicle.Plugin.FanartTV\
```

The plugin references `Chronicle.Plugins` via a local project reference marked `Private="false"` so the host's copy is used at runtime rather than a copy in the plugin output directory.

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.fanarttv"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\*.dll" $pluginDir
Copy-Item "manifest.json"           $pluginDir
```
