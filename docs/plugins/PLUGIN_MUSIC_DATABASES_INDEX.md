# Music Database Plugins — Index

**Source:** [List of online music databases (Wikipedia)](https://en.wikipedia.org/wiki/List_of_online_music_databases)

Each plugin in this index is a **separate repository** outside of the main Chronicle
repo, following the same scaffold pattern as all other Chronicle plugins.

> **Convention:** Repo name = `Chronicle.Plugin.{Name}` at `W:\Scripts\`.

---

## Explicitly Planned (separate docs exist)

| Plugin | Repo | Scope | Auth |
|--------|------|-------|------|
| [Discogs](PLUGIN_DISCOGS.md) | `Chronicle.Plugin.Discogs` | Releases, artists | OAuth / token |
| [TheAudioDB](PLUGIN_THEAUDIODB.md) | `Chronicle.Plugin.TheAudioDB` | Artists, albums | API key |
| [Soundcharts](PLUGIN_SOUNDCHARTS.md) | `Chronicle.Plugin.Soundcharts` | Analytics | API key + secret |
| [Jaxsta](PLUGIN_JAXSTA.md) | `Chronicle.Plugin.Jaxsta` | Credits | API key |
| [OneMusicAPI](PLUGIN_ONEMUSICAPI.md) | `Chronicle.Plugin.OneMusicAPI` | Aggregated | API key |

---

## From Wikipedia List — Planned Separate Repos

The following databases appear on the Wikipedia list of online music databases.
Each warrants its own Chronicle plugin repository. Listed with known API status.

### Comprehensive / Multi-genre

| Database | Repo | API Status | Notes |
|----------|------|-----------|-------|
| MusicBrainz | `Chronicle.Plugin.MusicBrainz` | ✅ Free, open | Already scaffolded |
| AllMusic | `Chronicle.Plugin.AllMusic` | ❌ No public API | Scraping only |
| Last.fm | `Chronicle.Plugin.LastFM` | ✅ Free API key | Scrobbling + metadata |
| Rate Your Music / Sonemic | `Chronicle.Plugin.RateYourMusic` | ❌ No API | Scraping only |
| Wikidata / Wikipedia | `Chronicle.Plugin.Wikidata` | ✅ Free SPARQL | Via wikidata.org |
| ListenBrainz | `Chronicle.Plugin.ListenBrainz` | ✅ Free | Scrobbling + stats |
| Setlist.fm | `Chronicle.Plugin.Setlistfm` | ✅ API key | Live setlists |
| SoundCloud | `Chronicle.Plugin.SoundCloud` | ✅ OAuth | Tracks, playlists |
| Bandcamp | `Chronicle.Plugin.Bandcamp` | ⚠️ Limited | Fan API + scraping |
| Genius | `Chronicle.Plugin.Genius` | ✅ API key | Lyrics + annotations |
| AZLyrics | `Chronicle.Plugin.AZLyrics` | ❌ No API | Scraping only |
| Musixmatch | `Chronicle.Plugin.Musixmatch` | ✅ API key | Lyrics + metadata |

### Streaming Services

| Database | Repo | API Status | Notes |
|----------|------|-----------|-------|
| Spotify | `Chronicle.Plugin.Spotify` | ✅ OAuth / client creds | Full metadata + audio features |
| Apple Music | `Chronicle.Plugin.AppleMusic` | ✅ MusicKit / API key | Catalog search |
| Deezer | `Chronicle.Plugin.Deezer` | ✅ Free (limited) | Tracks, albums, artists |
| Tidal | `Chronicle.Plugin.Tidal` | ✅ OAuth | Hi-res catalog |
| Amazon Music | `Chronicle.Plugin.AmazonMusic` | ⚠️ PA-API only | Via product catalogue |
| YouTube Music | `Chronicle.Plugin.YouTubeMusic` | ✅ YouTube Data API | Via YT video metadata |
| Qobuz | `Chronicle.Plugin.Qobuz` | ✅ API key | Hi-res catalog |

### Electronic / Dance / Club

| Database | Repo | API Status | Notes |
|----------|------|-----------|-------|
| Beatport | `Chronicle.Plugin.Beatport` | ✅ API key | EDM focus |
| Traxsource | `Chronicle.Plugin.Traxsource` | ⚠️ Limited | House/soul focus |
| Junodownload | `Chronicle.Plugin.Junodownload` | ❌ No API | Scraping only |

### Classical / Specialist

| Database | Repo | API Status | Notes |
|----------|------|-----------|-------|
| AllMusic Classical | `Chronicle.Plugin.AllMusicClassical` | ❌ No API | Part of AllMusic |
| Presto Music | `Chronicle.Plugin.PrestoMusic` | ❌ No API | Classical focus |
| ClassicCat | `Chronicle.Plugin.ClassicCat` | ❌ No API | Classical metadata |

### Credits / Industry

| Database | Repo | API Status | Notes |
|----------|------|-----------|-------|
| ASCAP ACE | `Chronicle.Plugin.ASCAP` | ✅ API key | Publishing / performance rights |
| BMI Repertoire | `Chronicle.Plugin.BMI` | ✅ Search API | Performance rights |
| SESAC | `Chronicle.Plugin.SESAC` | ❌ No public API | PRO |
| ISRC.net | `Chronicle.Plugin.ISRC` | ⚠️ Limited | ISRC code lookup |
| GRid | `Chronicle.Plugin.GRid` | ⚠️ Industry only | Global Release Identifier |

### Regional / Specialist Charts

| Database | Repo | API Status | Notes |
|----------|------|-----------|-------|
| Billboard | `Chronicle.Plugin.Billboard` | ⚠️ Partner only | US charts |
| Official Charts (UK) | `Chronicle.Plugin.OfficialCharts` | ⚠️ Limited | UK charts |
| IFPI | `Chronicle.Plugin.IFPI` | ❌ No API | Global industry body |
| Metacritic Music | `Chronicle.Plugin.MetacriticMusic` | ❌ No API | Scraping only |

---

## Plugin Priority Guidance for Music Media Type

When multiple music plugins are installed, recommended priority order:

1. **MusicBrainz** — authoritative IDs and structured data (already scaffolded)
2. **Discogs** — best for physical releases / vinyl
3. **TheAudioDB** — best for artwork
4. **Spotify** — best for audio features and popularity data
5. **OneMusicAPI** — aggregated fallback
6. **Last.fm** — community tags and scrobble counts
7. **Jaxsta** — credits (for users with subscription)
8. **Soundcharts** — analytics (for users with subscription)

---

## Implementation Status Key

| Symbol | Meaning |
|--------|---------|
| ✅ | Public API available |
| ⚠️ | API available with restrictions / partner program |
| ❌ | No public API — scraping required |
