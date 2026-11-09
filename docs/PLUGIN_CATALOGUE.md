# Chronicle Plugin Catalogue

**Last Updated:** 2026-11-06
**Author:** Chronicle Contributors with Anthropic Claude

This document indexes every planned metadata provider plugin for Chronicle.
Each plugin lives in its own separate GitHub repository as a sibling to the main
Chronicle repo.

Plugins that implement `IImportProvider` in addition to `IMetadataProvider`
are noted accordingly.

---

## ✅ Already Built & Deployed

These plugins are implemented. Their status is audited by `scripts/audit-plugins.py` (`docs/plugins/PLUGIN_COMPLIANCE.md`). Goodreads, Kodi.NFO and Letterboxd have code but are **not developed** by decision (2026-11-06); every other repo named below the table is an unbuilt scaffold.

| Plugin | Repository | Version | Interfaces | Media types | Needs | Source terms |
|---|---|---|---|---|---|---|
| **AniList** | [Chronicle.Plugin.AniList](https://github.com/thegoddamnbeckster/Chronicle.Plugin.AniList) | 1.0.6 | `IMetadataProvider` | anime, anime_movies | None. Public GraphQL API. | Free API; see AniList's terms before any commercial use, and keep the request rate polite. |
| **Deezer** | [Chronicle.Plugin.Deezer](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Deezer) | 1.0.3 | `IMetadataProvider` | music | None for public metadata. | Public API; see Deezer's terms for artwork and previews. |
| **FanEdit** | [Chronicle.Plugin.FanEdit](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FanEdit) | 1.0.3 | `IMetadataProvider` | n/a | A free fanedit.org account (username and password). | Reads the Fan Edit Database as the owner; do not redistribute its content. |
| **Fanart.tv** | [Chronicle.Plugin.FanartTV](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FanartTV) | 1.1.4 | `IMetadataProvider` | n/a | A Fanart.tv API key; an optional personal client key raises the limits. | Artwork belongs to its uploaders; the project asks for attribution. |
| **File Scanner** | [Chronicle.Plugin.FileScanner](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FileScanner) | 1.3.2 | `IMetadataProvider` | n/a | None. Reads local folders only. | Local files only; nothing is sent anywhere. |
| **Hardcover** | [Chronicle.Plugin.Hardcover](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Hardcover) | 1.7.0 | `IImportProvider + IMetadataProvider` | n/a | A Hardcover API token (Hardcover.app, Settings, API). | Reads the owner's own Hardcover account; see Hardcover's API terms. |
| **IGDB** | [Chronicle.Plugin.IGDB](https://github.com/thegoddamnbeckster/Chronicle.Plugin.IGDB) | 1.1.2 | `IMetadataProvider` | game | A Twitch developer Client ID and Client Secret. | Subject to the Twitch Developer Services Agreement; IGDB asks for attribution. |
| **IMDb** | [Chronicle.Plugin.IMDb](https://github.com/thegoddamnbeckster/Chronicle.Plugin.IMDb) | 1.2.2 | `IMetadataProvider` | n/a | None. Uses IMDb's published non-commercial datasets. | Personal, non-commercial use only; the datasets must not be redistributed. |
| **Last.fm** | [Chronicle.Plugin.LastFM](https://github.com/thegoddamnbeckster/Chronicle.Plugin.LastFM) | 1.1.2 | `IImportProvider + IMetadataProvider` | n/a | A Last.fm API key; scrobble import also needs the owner's username. | Last.fm API terms; keep to its rate limit. |
| **Movies Remastered (MRDb)** | [Chronicle.Plugin.MoviesRemastered](https://github.com/thegoddamnbeckster/Chronicle.Plugin.MoviesRemastered) | 1.1.1 | `IMetadataProvider` | n/a | None. Reads the public pages of moviesremastered.com. | A scraped site: the plugin paces itself and identifies itself; be gentle. |
| **MusicBrainz** | [Chronicle.Plugin.MusicBrainz](https://github.com/thegoddamnbeckster/Chronicle.Plugin.MusicBrainz) | 1.4.2 | `IMetadataProvider` | n/a | None. An optional MusicBrainz username and password are supported. | Core data is CC0. One request per second and a descriptive User-Agent are required. |
| **OMDb** | [Chronicle.Plugin.OMDb](https://github.com/thegoddamnbeckster/Chronicle.Plugin.OMDb) | 1.0.3 | `IMetadataProvider` | movies, tv, anime, anime_movies | An OMDb API key (the free one allows 1,000 requests a day). | OMDb's terms; the daily limit is honoured and reported. |
| **Open Library** | [Chronicle.Plugin.OpenLibrary](https://github.com/thegoddamnbeckster/Chronicle.Plugin.OpenLibrary) | 1.0.4 | `IMetadataProvider` | books, audiobooks | None. An optional contact address goes into the User-Agent. | Open data; Open Library asks heavy users to identify themselves. |
| **SIMKL** | [Chronicle.Plugin.Simkl](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Simkl) | 1.7.1 | `IImportProvider + IMetadataProvider` | movie, movies, tv, anime | A SIMKL client ID, then a sign-in from the plugin settings. | SIMKL API terms; the plugin respects its rate limits. |
| **Steam** | [Chronicle.Plugin.Steam](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Steam) | 1.1.2 | `IImportProvider + IMetadataProvider` | game | A Steam Web API key and the owner's Steam ID; the profile's game details must be public. | Steam Web API terms; the store is paced at about 200 requests per 5 minutes. |
| **TMDB** | [Chronicle.Plugin.TMDB](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB) | 1.9.2 | `IMetadataProvider` | n/a | A TMDB API key. | Free for non-commercial use; TMDB requires attribution and its logo where its data is shown. |
| **TVMaze** | [Chronicle.Plugin.TVMaze](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TVMaze) | 1.0.6 | `IMetadataProvider` | n/a | None. Public REST API. | Data is CC BY-SA; the API allows 20 calls per 10 seconds. |
| **TheAudioDB** | [Chronicle.Plugin.TheAudioDB](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TheAudioDB) | 1.0.1 | `IMetadataProvider` | music | A TheAudioDB API key (the free key 123 is the default; a premium key lifts the limits). | The free key returns one record per search and is rate limited; see TheAudioDB's terms. |
| **TheTVDB** | [Chronicle.Plugin.TheTVDB](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TheTVDB) | 1.0.1 | `IMetadataProvider` | n/a | A free TheTVDB API key from thetvdb.com/dashboard. | See TheTVDB's terms of use. |
| **Default Themes** | [Chronicle.Plugin.Themes.Default](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Themes.Default) | 1.0.1 | `IMetadataProvider` | n/a | None. Four built-in colour themes. | Ships with Chronicle. |
| **Trakt** | [Chronicle.Plugin.Trakt](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Trakt) | 1.2.2 | `IMetadataProvider` | n/a | A Trakt Client ID and Secret, then a sign-in from the plugin settings. | Trakt API terms; its rate limits are honoured. |
| **Wikipedia** | [Chronicle.Plugin.Wikipedia](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Wikipedia) | 1.0.18 | `IMetadataProvider` | n/a | None. Public MediaWiki API. | Text is CC BY-SA; attribute it. Chronicle ranks it last because articles vary in quality. |

---

## Planned Plugins

The rest of this document indexes planned future plugins.

---

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
| [IMDb](plugins/PLUGIN_IMDB.md) | `Chronicle.Plugin.IMDb` | 📺🎬👤 | None (official non-commercial datasets) | Designed |
| [JustWatch](plugins/PLUGIN_JUSTWATCH.md) | `Chronicle.Plugin.JustWatch` | 📺🎬 | None (public GraphQL) | Scaffolded |
| [Rotten Tomatoes](plugins/PLUGIN_ROTTENTOMATOES.md) | `Chronicle.Plugin.RottenTomatoes` | 📺🎬 | n/a | ❌ Rejected (Terms of Use forbid automated access) |
| Letterboxd | `Chronicle.Plugin.Letterboxd` | 🎬 | None (public RSS, import only) | ⛔ Not developed (2026-11-09). Code exists (RSS diary/ratings/watchlist import), never released. Not a metadata source: no public API (access by request only, personal projects not granted), Terms of Use forbid scraping, and its film data is TMDB's. Revisit only if API access is granted. |
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

### Catalogue & Cross-Reference

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [Discogs](plugins/PLUGIN_DISCOGS.md) | `Chronicle.Plugin.Discogs` | 🎵 | OAuth / token | Scaffolded |
| [TheAudioDB](plugins/PLUGIN_THEAUDIODB.md) | `Chronicle.Plugin.TheAudioDB` | 🎵 | API key (free key 123) | Implemented (1.0.0) |
| [Soundcharts](plugins/PLUGIN_SOUNDCHARTS.md) | `Chronicle.Plugin.Soundcharts` | 🎵 | API key + secret | Scaffolded |
| [Jaxsta](plugins/PLUGIN_JAXSTA.md) | `Chronicle.Plugin.Jaxsta` | 🎵 | API key | Scaffolded |
| [OneMusicAPI](plugins/PLUGIN_ONEMUSICAPI.md) | `Chronicle.Plugin.OneMusicAPI` | 🎵 | API key | Scaffolded |
| [MusicBrainz](plugins/PLUGIN_MUSICBRAINZ.md) | `Chronicle.Plugin.MusicBrainz` | 🎵 | None (public) | ✅ Built (see above) |

### Comprehensive / Multi-genre

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [AllMusic](plugins/PLUGIN_ALLMUSIC.md) | `Chronicle.Plugin.AllMusic` | 🎵 | None (scraping) | Scaffolded |
| [Last.fm](plugins/PLUGIN_LASTFM.md) | `Chronicle.Plugin.LastFM` | 🎵 | API key (free) | Scaffolded |
| [RateYourMusic](plugins/PLUGIN_RATEYOURMUSIC.md) | `Chronicle.Plugin.RateYourMusic` | 🎵 | None (scraping) | Scaffolded |
| [Wikidata](plugins/PLUGIN_WIKIDATA.md) | `Chronicle.Plugin.Wikidata` | 🎵 | None (public SPARQL) | Scaffolded |
| [ListenBrainz](plugins/PLUGIN_LISTENBRAINZ.md) | `Chronicle.Plugin.ListenBrainz` | 🎵 | API token (free) | Scaffolded |
| [Setlist.fm](plugins/PLUGIN_SETLISTFM.md) | `Chronicle.Plugin.Setlistfm` | 🎵 | API key (free) | Scaffolded |
| [SoundCloud](plugins/PLUGIN_SOUNDCLOUD.md) | `Chronicle.Plugin.SoundCloud` | 🎵 | OAuth 2.0 | Scaffolded |
| [Bandcamp](plugins/PLUGIN_BANDCAMP.md) | `Chronicle.Plugin.Bandcamp` | 🎵 | None (scraping) | Scaffolded |
| [Genius](plugins/PLUGIN_GENIUS.md) | `Chronicle.Plugin.Genius` | 🎵 | API token (free) | Scaffolded |
| [AZLyrics](plugins/PLUGIN_AZLYRICS.md) | `Chronicle.Plugin.AZLyrics` | 🎵 | None (scraping) | Scaffolded |
| [Musixmatch](plugins/PLUGIN_MUSIXMATCH.md) | `Chronicle.Plugin.Musixmatch` | 🎵 | API key (free tier) | Scaffolded |

### Streaming Services

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [Spotify](plugins/PLUGIN_SPOTIFY.md) | `Chronicle.Plugin.Spotify` | 🎵 | OAuth 2.0 Client Creds | Scaffolded |
| [Apple Music](plugins/PLUGIN_APPLEMUSIC.md) | `Chronicle.Plugin.AppleMusic` | 🎵 | MusicKit JWT | Scaffolded |
| [Deezer](plugins/PLUGIN_DEEZER.md) | `Chronicle.Plugin.Deezer` | 🎵 | None (public endpoints) | Scaffolded |
| [Tidal](plugins/PLUGIN_TIDAL.md) | `Chronicle.Plugin.Tidal` | 🎵 | OAuth 2.0 | Scaffolded |
| [Amazon Music](plugins/PLUGIN_AMAZONMUSIC.md) | `Chronicle.Plugin.AmazonMusic` | 🎵 | AWS SigV4 (optional) | Scaffolded |
| [YouTube Music](plugins/PLUGIN_YOUTUBEMUSIC.md) | `Chronicle.Plugin.YouTubeMusic` | 🎵 | YouTube Data API key | Scaffolded |
| [Qobuz](plugins/PLUGIN_QOBUZ.md) | `Chronicle.Plugin.Qobuz` | 🎵 | app_id + HMAC secret | Scaffolded |

### Electronic / Dance Music

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [Beatport](plugins/PLUGIN_BEATPORT.md) | `Chronicle.Plugin.Beatport` | 🎵 | OAuth 2.0 | Scaffolded |
| [Traxsource](plugins/PLUGIN_TRAXSOURCE.md) | `Chronicle.Plugin.Traxsource` | 🎵 | None (unofficial JSON) | Scaffolded |
| [Juno Download](plugins/PLUGIN_JUNODOWNLOAD.md) | `Chronicle.Plugin.JunoDownload` | 🎵 | None (scraping) | Scaffolded |

### Classical Music

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [AllMusic Classical](plugins/PLUGIN_ALLMUSIC_CLASSICAL.md) | `Chronicle.Plugin.AllMusicClassical` | 🎵 | None (scraping) | Scaffolded |
| [Presto Music](plugins/PLUGIN_PRESTOMUSIC.md) | `Chronicle.Plugin.PrestoMusic` | 🎵 | None (scraping) | Scaffolded |
| [ClassicCat](plugins/PLUGIN_CLASSICCAT.md) | `Chronicle.Plugin.ClassicCat` | 🎵 | None (public) | Scaffolded |

### Credits & Industry Standards

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [ASCAP](plugins/PLUGIN_ASCAP.md) | `Chronicle.Plugin.ASCAP` | 🎵 | None (public ACE) | Scaffolded |
| [BMI](plugins/PLUGIN_BMI.md) | `Chronicle.Plugin.BMI` | 🎵 | None (public repertoire) | Scaffolded |
| [SESAC](plugins/PLUGIN_SESAC.md) | `Chronicle.Plugin.SESAC` | 🎵 | None (public repertoire) | Scaffolded |
| [ISRC Registry](plugins/PLUGIN_ISRC.md) | `Chronicle.Plugin.ISRC` | 🎵 | None (IFPI portal) | Scaffolded |
| [GRid Registry](plugins/PLUGIN_GRID.md) | `Chronicle.Plugin.GRid` | 🎵 | None (DDEX portal) | Scaffolded |

### Charts

| Plugin | Directory | Scope | Auth | Status |
|--------|-----------|-------|------|--------|
| [Billboard](plugins/PLUGIN_BILLBOARD.md) | `Chronicle.Plugin.Billboard` | 🎵 | None (scraping) | Scaffolded |
| [Official Charts (UK)](plugins/PLUGIN_OFFICIALCHARTS.md) | `Chronicle.Plugin.OfficialCharts` | 🎵 | None (scraping) | Scaffolded |
| [IFPI Global Charts](plugins/PLUGIN_IFPI.md) | `Chronicle.Plugin.IFPI` | 🎵 | None (scraping) | Scaffolded |
| [Metacritic Music](plugins/PLUGIN_METACRITICMUSIC.md) | `Chronicle.Plugin.MetacriticMusic` | 🎵 | None (scraping) | Scaffolded |

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
| [IGDB](plugins/PLUGIN_IGDB.md) | `Chronicle.Plugin.IGDB` | 🎮 | Twitch client ID + secret | Implemented and released (1.0.0, 2026-11-05) |
| [RAWG](plugins/PLUGIN_RAWG.md) | `Chronicle.Plugin.RAWG` | 🎮 | API key (free) | Scaffolded |
| [Gameopedia](plugins/PLUGIN_GAMEOPEDIA.md) | `Chronicle.Plugin.Gameopedia` | 🎮 | API key (commercial) | Scaffolded |
| [LaunchBox Games DB](plugins/PLUGIN_LAUNCHBOX.md) | `Chronicle.Plugin.LaunchBox` | 🎮 | API key | Scaffolded |
| [Metadata Games (Tiltfactor)](plugins/PLUGIN_TILTFACTOR.md) | `Chronicle.Plugin.TiltfactorGames` | 🎮 | None (public) | Scaffolded |
| [Video Game Resources (AMIA)](plugins/PLUGIN_VIDEOGAMERESOURCES.md) | `Chronicle.Plugin.VideoGameResources` | 🎮 | None (GitHub) | Scaffolded |
| [Steam](plugins/PLUGIN_STEAM.md) | `Chronicle.Plugin.Steam` | 🎮 | Steam API key (free) | Implemented (1.0.0, 2026-11-05): library import, wishlist, level, badges, store metadata, achievements |
| [IsThereAnyDeal](plugins/PLUGIN_ISTHEREANYDEAL.md) | `Chronicle.Plugin.IsThereAnyDeal` | 🎮 | API key (free) | Scaffolded |

---

## Summary

| Category | Count |
|----------|-------|
| TV & Film | 12 |
| General / Web | 7 |
| Music — Catalogue & Cross-Reference | 6 |
| Music — Comprehensive / Multi-genre | 11 |
| Music — Streaming | 7 |
| Music — Electronic / Dance | 3 |
| Music — Classical | 3 |
| Music — Credits & Industry | 5 |
| Music — Charts | 4 |
| Books | 10 |
| Games | 8 |
| **Total** | **76** |

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

---

## Scrobbler Convention

Scrobblers are standalone services (not metadata plugins) that watch an
external media player and push progress events to Chronicle.

```
Chronicle.Service.Scrobbler.{App}/
├── service.py              # entry point
├── config.ini.example
├── README.md
└── lib/
    ├── chronicle_client.py
    ├── progress_tracker.py
    └── ...
```

| Scrobbler | Directory | Target | Language |
|-----------|-----------|--------|----------|
| Kodi | `Chronicle.Service.Scrobbler.Kodi` | Kodi Media Center | Python (Kodi addon) |
| AudiobookShelf | `Chronicle.Service.Scrobbler.Audiobookshelf` | AudiobookShelf | Python (standalone) |

**Naming rule:** `Chronicle.Service.Scrobbler.<App>` (PascalCase)
