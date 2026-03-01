# Chronicle Plugin Catalogue

**Last Updated:** 2026-02-28
**Author:** Chronicle Contributors with Anthropic Claude

This document indexes every planned metadata provider plugin for Chronicle.
Each plugin lives in its own directory at the repository root
(e.g., `Chronicle.Plugin.IGDB/`) and is intended to be published as a
separate GitHub repository.

Plugins that implement `IImportProvider` instead of `IMetadataProvider`
are noted accordingly.

---

## Media-Type Scope Legend

| Symbol | Media Type |
|--------|-----------|
| 📺 | TV / Series |
| 🎬 | Movies |
| 🎵 | Music / Albums / Artists |
| 📚 | Books / Audiobooks |
| 🎮 | Games |
| 🌐 | Web / Generic metadata |
| 📡 | EPG / Broadcast schedules |

---

## TV & Film Plugins

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [TheTVDB](plugins/PLUGIN_THETVDB.md) | `Chronicle.Plugin.TheTVDB` | 📺 | API key (free) | Scaffolded |
| [Gracenote](plugins/PLUGIN_GRACENOTE.md) | `Chronicle.Plugin.Gracenote` | 📺🎬🎵 | Partner credentials | Scaffolded |
| [IMDb](plugins/PLUGIN_IMDB.md) | `Chronicle.Plugin.IMDb` | 📺🎬 | IMDb API key | Scaffolded |
| [JustWatch](plugins/PLUGIN_JUSTWATCH.md) | `Chronicle.Plugin.JustWatch` | 📺🎬 | None (public GraphQL) | Scaffolded |
| [Rotten Tomatoes](plugins/PLUGIN_ROTTENTOMATOES.md) | `Chronicle.Plugin.RottenTomatoes` | 📺🎬 | API key (partner) | Scaffolded |
| [PA TV Metadata](plugins/PLUGIN_PATVMETADATA.md) | `Chronicle.Plugin.PATVMetadata` | 📺📡 | API key | Scaffolded |
| [InforPortugal](plugins/PLUGIN_INFORPORTUGAL.md) | `Chronicle.Plugin.InforPortugal` | 📺📡 | API key | Scaffolded |
| [EPGdata.tv](plugins/PLUGIN_EPGDATA.md) | `Chronicle.Plugin.EPGData` | 📺📡 | API key | Scaffolded |
| [Simply.tv](plugins/PLUGIN_SIMPLYTV.md) | `Chronicle.Plugin.SimplyTV` | 📺📡 | API key | Scaffolded |
| [metaprofile.tv](plugins/PLUGIN_METAPROFILE.md) | `Chronicle.Plugin.MetaProfile` | 📺🎬 | API key | Scaffolded |
| [media-press.tv](plugins/PLUGIN_MEDIAPRESS.md) | `Chronicle.Plugin.MediaPress` | 📺🎬 | API key | Scaffolded |
| [TinyMediaManager](plugins/PLUGIN_TINYMEDIAMANAGER.md) | `Chronicle.Plugin.TinyMediaManager` | 📺🎬 | Local URL | Scaffolded |

---

## General / Web Metadata Plugins

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [Internet Archive](plugins/PLUGIN_INTERNETARCHIVE.md) | `Chronicle.Plugin.InternetArchive` | 🌐📺🎬🎵📚🎮 | None (public) | Scaffolded |
| [Open Movie Database (OMDb)](plugins/PLUGIN_OMDB.md) | `Chronicle.Plugin.OMDb` | 🎬📺 | API key (free tier) | Scaffolded |
| [Exif Info](plugins/PLUGIN_EXIFINFO.md) | `Chronicle.Plugin.ExifInfo` | 🌐 | None | Scaffolded |
| [Metadata2Go](plugins/PLUGIN_METADATA2GO.md) | `Chronicle.Plugin.Metadata2Go` | 🌐 | None | Scaffolded |
| [OpenGraph.xyz](plugins/PLUGIN_OPENGRAPH.md) | `Chronicle.Plugin.OpenGraph` | 🌐 | None | Scaffolded |
| [Meta Tags (metatags.io)](plugins/PLUGIN_METATAGS.md) | `Chronicle.Plugin.MetaTags` | 🌐 | None | Scaffolded |
| [web.dev](plugins/PLUGIN_WEBDEV.md) | `Chronicle.Plugin.WebDev` | 🌐 | None | Scaffolded |

---

## Music Plugins

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [Discogs](plugins/PLUGIN_DISCOGS.md) | `Chronicle.Plugin.Discogs` | 🎵 | OAuth / token | Scaffolded |
| [TheAudioDB](plugins/PLUGIN_THEAUDIODB.md) | `Chronicle.Plugin.TheAudioDB` | 🎵 | API key (free tier) | Scaffolded |
| [Soundcharts](plugins/PLUGIN_SOUNDCHARTS.md) | `Chronicle.Plugin.Soundcharts` | 🎵 | API key + secret | Scaffolded |
| [Jaxsta](plugins/PLUGIN_JAXSTA.md) | `Chronicle.Plugin.Jaxsta` | 🎵 | API key | Scaffolded |
| [OneMusicAPI](plugins/PLUGIN_ONEMUSICAPI.md) | `Chronicle.Plugin.OneMusicAPI` | 🎵 | API key | Scaffolded |

> **Note:** Additional music database plugins sourced from
> [List of online music databases (Wikipedia)](https://en.wikipedia.org/wiki/List_of_online_music_databases)
> are planned. Each will be its own separate repository outside of Chronicle.
> See `docs/plugins/PLUGIN_MUSIC_DATABASES_INDEX.md` for the full list.

---

## Book Plugins

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [ISBNdb](plugins/PLUGIN_ISBNDB.md) | `Chronicle.Plugin.ISBNdb` | 📚 | API key | Scaffolded |
| [Bowker](plugins/PLUGIN_BOWKER.md) | `Chronicle.Plugin.Bowker` | 📚 | API key (commercial) | Scaffolded |
| [IngramSpark](plugins/PLUGIN_INGRAMSPARK.md) | `Chronicle.Plugin.IngramSpark` | 📚 | API key (publisher acct) | Scaffolded |
| [WorldCat](plugins/PLUGIN_WORLDCAT.md) | `Chronicle.Plugin.WorldCat` | 📚 | WSKey / API key | Scaffolded |
| [Open Library](plugins/PLUGIN_OPENLIBRARY.md) | `Chronicle.Plugin.OpenLibrary` | 📚 | None (public) | Scaffolded |
| [Google Books](plugins/PLUGIN_GOOGLEBOOKS.md) | `Chronicle.Plugin.GoogleBooks` | 📚 | API key (free) | Scaffolded |
| [Crossref](plugins/PLUGIN_CROSSREF.md) | `Chronicle.Plugin.Crossref` | 📚 | None / Polite pool email | Scaffolded |
| [Audiobookshelf](plugins/PLUGIN_AUDIOBOOKSHELF.md) | `Chronicle.Plugin.Audiobookshelf` | 📚 | Local URL + API key | Scaffolded |
| [Amazon Books](plugins/PLUGIN_AMAZON.md) | `Chronicle.Plugin.Amazon` | 📚 | PA API key + secret | Scaffolded |
| [Barnes & Noble](plugins/PLUGIN_BARNESANDNOBLE.md) | `Chronicle.Plugin.BarnesAndNoble` | 📚 | API key | Scaffolded |

---

## Game Plugins

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [IGDB](plugins/PLUGIN_IGDB.md) | `Chronicle.Plugin.IGDB` | 🎮 | Twitch client ID + secret | Scaffolded |
| [RAWG](plugins/PLUGIN_RAWG.md) | `Chronicle.Plugin.RAWG` | 🎮 | API key (free) | Scaffolded |
| [Gameopedia](plugins/PLUGIN_GAMEOPEDIA.md) | `Chronicle.Plugin.Gameopedia` | 🎮 | API key (commercial) | Scaffolded |
| [LaunchBox Games DB](plugins/PLUGIN_LAUNCHBOX.md) | `Chronicle.Plugin.LaunchBox` | 🎮 | API key | Scaffolded |
| [Metadata Games (Tiltfactor)](plugins/PLUGIN_TILTFACTOR.md) | `Chronicle.Plugin.TiltfactorGames` | 🎮 | None (public) | Scaffolded |
| [Video Game Resources (AMIA)](plugins/PLUGIN_VIDEOGAMERESOURCES.md) | `Chronicle.Plugin.VideoGameResources` | 🎮 | None (GitHub) | Scaffolded |
| [Steam](plugins/PLUGIN_STEAM.md) | `Chronicle.Plugin.Steam` | 🎮 | Steam API key (free) | Scaffolded |
| [IsThereAnyDeal](plugins/PLUGIN_ISTHEREANYDEAL.md) | `Chronicle.Plugin.IsThereAnyDeal` | 🎮 | API key (free) | Scaffolded |

---

## Summary

| Category | Count |
|----------|-------|
| TV & Film | 12 |
| General / Web | 7 |
| Music | 5 + Wikipedia list |
| Books | 10 |
| Games | 8 |
| **Total (explicit)** | **42** |

---

## Repository Convention

Every plugin repository follows this structure:

```
Chronicle.Plugin.{Name}/
├── Chronicle.Plugin.{Name}.csproj
├── README.md               ← design document (this file per plugin)
├── manifest.json           ← Chronicle plugin manifest
├── {Name}Plugin.cs         ← IMetadataProvider stub
└── Models/                 ← API response models (added during implementation)
```

### Naming Rules

- **PluginId:** `chronicle.plugin.{name}` (lowercase, dot-separated)
- **Namespace:** `Chronicle.Plugin.{Name}` (PascalCase)
- **Assembly:** `Chronicle.Plugin.{Name}`
- **Entry type:** `Chronicle.Plugin.{Name}.{Name}Plugin`
