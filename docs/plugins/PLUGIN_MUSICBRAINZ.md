# MusicBrainz plugin

**Version:** 1.4.1  ·  **Repository:** `Chronicle.Plugin.MusicBrainz`  ·  **Media types:** n/a

**What it needs from the owner:** None. An optional MusicBrainz username and password are supported.

**The source's terms and limits:** Core data is CC0. One request per second and a descriptive User-Agent are required. (https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting)

The plugin's own README follows; the repository is the source of truth.

---

# Chronicle.Plugin.MusicBrainz

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.MusicBrainz?label=Chronicle.Plugin.MusicBrainz&color=ba478f)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.MusicBrainz/releases/latest)

MusicBrainz metadata provider plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Fetches music metadata — albums, artists, genres, track listings, and cover art — from [MusicBrainz](https://musicbrainz.org/) and the [Cover Art Archive](https://coverartarchive.org/). **No API key required.**

**Everything MusicBrainz lists is fetched.** A MusicBrainz lookup shows only 25 of any list (Elvis Presley: 1,363 release groups, 2,122 releases, 869 works and 35,767 recordings behind a lookup that showed 25 of each). The plugin browses each list a hundred at a time until the count MusicBrainz reports is reached: an artist keeps every release group, release, work and recording (and the cover art of every release group), a release group keeps every release in full (track listings, labels, barcodes) and every release's cover art, and a recording keeps every release it appears on. Every tag is kept. This takes many requests at MusicBrainz's one-a-second pace, so one large artist can take most of an hour: the manifest declares `call_timeout_seconds` (4 hours; needs a Chronicle build that reads it, otherwise the host's 25 seconds applies), and every page and lookup is remembered for six hours so a fetch that fails near its end resumes instead of starting over. A busy or failing Cover Art Archive is retried, never recorded as "no cover art".

## Supported Media Types

| Media Type | Fields |
|------------|--------|
| `music` (albums) | title, year, poster_url (cover art), genres, cast (performers), runtime_minutes |
| `music` (artists) | title, year, genres, overview (type, country, active period) |
| `audiobooks` | title, year, poster_url (cover art), overview, cast (author/narrator) |

## Audiobook Support

Chronicle stores audiobooks as flat items (no track hierarchy). The plugin searches MusicBrainz release groups filtered by `secondarytype:Audiobook`. When enriching audiobooks:

- The **author** is read from `fileScanner.author` in the item's metadata JSON, or derived from the parent directory of the stored book-folder path (for items imported before the author field was written).
- The **short title** is extracted from the audiobook folder name (e.g. `Series - 2 - (2021) - Short Title` → `"Short Title"`) and tried first, before the full AudioAlbum tag which may contain a publisher subtitle that MusicBrainz does not index.
- If no author can be found, the search is skipped (MusicBrainz audiobook search requires an artist to be useful).

## External ID Format

`{type}:{mbid}` where MBID is the MusicBrainz UUID:

- `release-group:f4179994-7621-4a46-b272-c62d8b3b9b1b` → a release group (album)
- `artist:5b11f4ce-a62d-471e-81fc-a69a8278c7da` → Nirvana
- `release:...` → a specific pressing/edition — automatically resolved to its parent release group

Fix Match accepts MusicBrainz URLs:
- `https://musicbrainz.org/release-group/f4179994-...`
- `https://musicbrainz.org/release/...` (resolved to release-group automatically)

## Installation

1. Build the plugin:
   ```powershell
   dotnet build -c Release
   ```

2. Copy `bin\Release\net9.0\*.dll` and `manifest.json` into your Chronicle `plugins\chronicle.plugin.musicbrainz\` directory.

3. Optionally configure a custom User-Agent string in plugin settings.

## Configuration

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `user_agent` | | `Chronicle/1.0 (...)` | MusicBrainz requires a descriptive User-Agent. See [Rate Limiting](https://wiki.musicbrainz.org/MusicBrainz_API/Rate_Limiting). |
| `fetch_cover_art` | | `true` | Download album art from the Cover Art Archive. |

> **Rate limiting:** MusicBrainz allows ~1 request/second for anonymous clients.

## Deploying

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.musicbrainz"
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
  Chronicle.Plugin.MusicBrainz\
```

The plugin references `Chronicle.Plugins` via a local project reference:

```xml
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
```

> **Important:** `Chronicle.Plugins.dll` must **not** be in the plugin output directory — the Chronicle host provides it. `<Private>false</Private>` prevents it from being copied.
