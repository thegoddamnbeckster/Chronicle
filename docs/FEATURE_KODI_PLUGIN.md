# Feature Design: Kodi Sync Plugin (`Chronicle.Plugin.Kodi`)

> **Superseded (2026-07-26):** `Chronicle_Scrobbler` (a Kodi-side addon modeled on
> SIMKL_Scrobbler, see `W:\Scripts\Chronicle_Scrobbler`) covers everything this
> document describes — ratings, play counts, last-played — plus live scrobbling
> and full bidirectional sync, without requiring Chronicle's server to reach into
> Kodi's network. This document is kept as a historical record of the alternative
> (server-push, `IMediaSyncPlugin`) architecture; it was never built. Revisit only
> if a use case emerges that specifically needs push-from-server rather than an
> installed Kodi addon (e.g. a user who can't or won't install Kodi addons).

**Status:** Design/Planning — superseded, not built
**Target:** Phase 3
**Goal:** Synchronise Chronicle's rich metadata (ratings, watch status, play counts, last-played dates) into a local Kodi instance via the Kodi JSON-RPC API. All Chronicle data — regardless of where it originated (Trakt import, manual scrobble, Letterboxd import, etc.) — is propagated to Kodi's library.

**Kodi Guidelines Reference:**
- [JSON-RPC API](https://kodi.wiki/view/JSON-RPC_API)
- [NFO Files](https://kodi.wiki/view/NFO_files)
- [Add-on guidelines](https://kodi.wiki/view/Add-on_rules)
- [Video library metadata](https://kodi.wiki/view/Video_library)

---

## Overview

Chronicle and Kodi serve complementary roles:

| Concern | Chronicle | Kodi |
|---------|-----------|------|
| Scrobbling / tracking | ✓ | via plugins only |
| Ratings (1–10) | ✓ | user ratings stored per item |
| Cross-source import | ✓ Trakt, Simkl, Letterboxd… | ✗ |
| Local file playback | ✗ | ✓ |
| In-room media centre UI | ✗ | ✓ |

The Kodi plugin is a **one-way sync**: Chronicle → Kodi. It pushes Chronicle's curated view of a user's media history into Kodi's library so that Kodi reflects accurate play counts, user ratings, and last-played timestamps — even for items the user watched through another service or device.

---

## New Plugin Interface: `IMediaSyncPlugin`

A new Chronicle plugin interface is needed for "push" integrations (exporting data to external systems). This interface lives in `Chronicle.Plugins`.

```csharp
namespace Chronicle.Plugins;

/// <summary>
/// A plugin that synchronises Chronicle data to an external media system
/// (Kodi, Plex, Jellyfin, Emby, etc.).
///
/// Unlike IImportProvider (pull), IMediaSyncPlugin is a push integration:
/// Chronicle is the source of truth and the external system receives updates.
/// </summary>
public interface IMediaSyncPlugin
{
    string PluginId    { get; }
    string Name        { get; }
    string Version     { get; }
    string Description { get; }

    PluginSettingsSchema GetSettingsSchema();
    void Configure(IReadOnlyDictionary<string, string> settings);

    /// <summary>Verifies that the plugin can connect to the external system.</summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Performs a sync of the provided Chronicle items to the external system.
    /// Implementations must be idempotent — running the same sync twice should
    /// produce the same result.
    /// </summary>
    Task<SyncResult> SyncAsync(SyncContext context, CancellationToken ct = default);
}

// ── Sync context types ─────────────────────────────────────────────────────────

public record SyncContext(
    /// <summary>Chronicle items to synchronise (pre-filtered by the sync service).</summary>
    IReadOnlyList<SyncableItem> Items
);

public record SyncableItem(
    /// <summary>"movie" | "tv_show" | "tv_episode" | "music_album" | "music_track"</summary>
    string MediaType,
    string Title,
    int? Year,
    /// <summary>Season number (tv_episode only).</summary>
    int? Season,
    /// <summary>Episode number (tv_episode only).</summary>
    int? Episode,
    /// <summary>
    /// Cross-reference IDs available for this item.
    /// Keys: "tmdb", "imdb", "tvdb", "musicbrainz_release", etc.
    /// </summary>
    IReadOnlyDictionary<string, string> ExternalIds,
    /// <summary>Chronicle user rating on 1–10 scale. Null = not rated.</summary>
    int? UserRating,
    /// <summary>Total number of times watched / listened.</summary>
    int PlayCount,
    /// <summary>Most recent play event timestamp. Null if never played.</summary>
    DateTimeOffset? LastPlayed,
    string LibraryStatus   // "watching" | "completed" | "dropped" | "plan_to_watch"
);

public record SyncResult(
    int Synced,
    int Skipped,
    int Failed,
    IReadOnlyList<string> Errors
);
```

### Registry Integration

`IPluginRegistry` gains:
```csharp
IReadOnlyList<IMediaSyncPlugin> GetSyncPlugins();
```

`LoadedPlugin` gains:
```csharp
IReadOnlyList<IMediaSyncPlugin> SyncPlugins { get; }
```

---

## Kodi JSON-RPC API

### Transport

Kodi exposes a JSON-RPC 2.0 API over HTTP:

```
POST http://{host}:{port}/jsonrpc
Content-Type: application/json
Authorization: Basic {base64(user:password)}   ← only if authentication enabled

{
  "jsonrpc": "2.0",
  "method": "VideoLibrary.GetMovies",
  "params": { "properties": ["imdbnumber", "title", "year", "userrating", "playcount", "lastplayed"] },
  "id": 1
}
```

**Default port:** 8080 (Kodi default; configurable in Kodi → Settings → Services → Control).
**Authentication:** Optional HTTP Basic Auth — can be disabled in Kodi settings.

### Key Methods Used

| Method | Purpose |
|--------|---------|
| `JSONRPC.Ping` | Connection test |
| `VideoLibrary.GetMovies` | Fetch all Kodi movies with identifiers |
| `VideoLibrary.SetMovieDetails` | Update movie metadata |
| `VideoLibrary.GetTVShows` | Fetch all Kodi TV shows |
| `VideoLibrary.SetTVShowDetails` | Update TV show metadata |
| `VideoLibrary.GetEpisodes` | Fetch episodes for a show |
| `VideoLibrary.SetEpisodeDetails` | Update episode metadata |
| `MusicLibrary.GetAlbums` | Fetch all Kodi music albums |
| `MusicLibrary.SetAlbumDetails` | Update album metadata |
| `MusicLibrary.GetSongs` | Fetch all songs |
| `MusicLibrary.SetSongDetails` | Update song metadata |

### Fields Synced

| Chronicle field | Kodi field | Notes |
|----------------|-----------|-------|
| `UserRating` (1–10) | `userrating` (1–10) | Scales match exactly |
| `PlayCount` | `playcount` | Integer |
| `LastPlayed` | `lastplayed` | Kodi format: `"2024-03-15 21:30:00"` |

---

## Matching Strategy

Kodi library items are matched to Chronicle items using a priority chain:

1. **IMDB ID** — `imdbnumber` in Kodi matches `imdb` in Chronicle's `ExternalIds`.
2. **TMDB ID** — Kodi stores TMDB IDs in `uniqueid` for items scraped with the TMDB scraper.
3. **TVDB ID** — Similarly via `uniqueid`.
4. **Title + Year** — Case-insensitive fuzzy match as a last resort.

Unmatched Chronicle items are logged and skipped (not forced into Kodi — Chronicle does not create Kodi library entries; that is Kodi's responsibility via its own scrapers).

### Matching Implementation

```csharp
// Pseudocode
foreach (var chronicleItem in context.Items)
{
    var kodiItem = kodiItems.FirstOrDefault(k =>
        MatchById(k, chronicleItem) ?? MatchByTitleYear(k, chronicleItem));

    if (kodiItem is null) { skipped++; continue; }

    await SetDetailsAsync(kodiItem.KodiId, chronicleItem, ct);
    synced++;
}
```

---

## Sync Modes

| Mode | Description |
|------|-------------|
| **Manual** | User triggers sync from Chronicle Plugins page |
| **Scheduled** | Chronicle syncs to Kodi on a configurable interval (e.g. every 6 hours) |
| **Post-scrobble** | After a scrobble is processed, the affected item is synced immediately |

The `ISyncSchedulerService` (new, Phase 3) manages scheduled syncs. For Phase 1 of the Kodi plugin, only Manual mode is implemented.

---

## Plugin Structure

```
W:\Scripts\Chronicle.Plugin.Kodi\
  Chronicle.Plugin.Kodi.csproj
  manifest.json
  KodiModels.cs            ← JSON-RPC request/response records
  KodiClient.cs            ← HTTP wrapper for Kodi JSON-RPC
  KodiSyncPlugin.cs        ← IMediaSyncPlugin implementation
```

---

## Settings Schema

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `host` | Text | `localhost` | Kodi host or IP address |
| `port` | Text | `8080` | Kodi JSON-RPC HTTP port |
| `username` | Text | _(empty)_ | Kodi username (if auth enabled) |
| `password` | Password | _(empty)_ | Kodi password (if auth enabled) |
| `sync_movies` | Toggle | `true` | Sync movie ratings and play counts |
| `sync_tvshows` | Toggle | `true` | Sync TV show and episode data |
| `sync_music` | Toggle | `true` | Sync album and track data |
| `sync_userrating` | Toggle | `true` | Push user ratings to Kodi |
| `sync_playcount` | Toggle | `true` | Push play counts to Kodi |
| `sync_lastplayed` | Toggle | `true` | Push last-played timestamps to Kodi |

---

## Data Flow

```
Chronicle DB
  ↓ (InteractionEvents + UserLibraries + ExternalIds)
SyncService.BuildSyncContextAsync(userId)
  ↓ (SyncableItem list with all metadata)
KodiSyncPlugin.SyncAsync()
  ↓
KodiClient.GetLibraryAsync()   ← fetches Kodi's current library
  ↓
Match Chronicle ↔ Kodi items
  ↓
KodiClient.SetDetailsAsync()   ← PATCH only changed fields
  ↓
SyncResult
```

### Kodi Library Fetch (once per sync)

```
GET /jsonrpc → VideoLibrary.GetMovies
  properties: [imdbnumber, uniqueid, title, year, userrating, playcount, lastplayed]

GET /jsonrpc → VideoLibrary.GetTVShows
  properties: [imdbnumber, uniqueid, title, year, userrating, playcount]

GET /jsonrpc → VideoLibrary.GetEpisodes (for each matched show)
  properties: [title, season, episode, userrating, playcount, lastplayed, showtitle]

GET /jsonrpc → MusicLibrary.GetAlbums
  properties: [title, artist, year, userrating, playcount]

GET /jsonrpc → MusicLibrary.GetSongs
  properties: [title, artist, album, year, userrating, playcount, lastplayed]
```

### Kodi Update (per matched item)

```csharp
// Only send fields that are enabled and have changed vs Kodi's current value
// (avoids redundant writes and respects Kodi's rate limits)
POST /jsonrpc → VideoLibrary.SetMovieDetails
{
  "movieid": 42,
  "userrating": 8,       // Chronicle rating
  "playcount": 3,        // Chronicle play count
  "lastplayed": "2024-03-15 21:30:00"
}
```

---

## NFO File Generation (Optional)

For Chronicle items that are **not** in Kodi's library (e.g. films watched on Netflix that Kodi has no local file for), the plugin can optionally generate `.nfo` stub files. This is **opt-in** and requires a configurable output directory.

NFO format for movies (Kodi movie.nfo):
```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>Movie Title</title>
  <year>2023</year>
  <uniqueid type="imdb" default="true">tt1234567</uniqueid>
  <uniqueid type="tmdb">67890</uniqueid>
  <userrating>8</userrating>
  <playcount>2</playcount>
  <lastplayed>2024-03-15</lastplayed>
</movie>
```

NFO generation is governed by an additional setting:

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `generate_nfo` | Toggle | `false` | Generate .nfo stubs for unmatched items |
| `nfo_output_dir` | FilePath | _(empty)_ | Directory where .nfo files are written |

Kodi guidelines require that `.nfo` files use the correct schema. Chronicle follows the [Kodi NFO file format](https://kodi.wiki/view/NFO_files/Movies) exactly.

---

## Rate Limiting & Kodi Guidelines Compliance

The [Kodi JSON-RPC API](https://kodi.wiki/view/JSON-RPC_API) has no documented rate limit, but Kodi is a local application so network latency is minimal. However, to avoid overwhelming Kodi during large syncs:

- Batch reads first (fetch all library items in one call each)
- Batch writes using Kodi's built-in sequencing (one `SetDetails` call per item, 50ms delay between calls)
- Never call `VideoLibrary.Clean` or `VideoLibrary.Scan` — only update existing items
- Respect the `playcount` field: Chronicle sets it but does not forcibly reset it to 0

The plugin **never**:
- Deletes Kodi library entries
- Triggers a Kodi library scan
- Modifies file paths or sources
- Changes Kodi's scraped metadata (title, plot, cast — only user-specific data: rating, playcount, lastplayed)

---

## API Endpoint

A new Chronicle API endpoint triggers a manual sync:

```
POST /api/v1/sync/{pluginId}?userId={userId}
→ { "synced": 47, "skipped": 12, "failed": 0, "errors": [] }
```

Registered via a new `SyncController` and backed by `ISyncService`.

---

## Implementation Order

### Phase 1 (MVP Kodi Plugin)
1. Add `IMediaSyncPlugin` interface to `Chronicle.Plugins`
2. Extend `LoadedPlugin` and `IPluginRegistry` with `GetSyncPlugins()`
3. Create `ISyncService` + `SyncService` in `Chronicle.Services`
4. Add `POST /api/v1/sync/{pluginId}` endpoint
5. Create `Chronicle.Plugin.Kodi` project
6. `KodiClient.cs` — JSON-RPC HTTP wrapper with Basic Auth
7. `KodiModels.cs` — request/response records for all library methods
8. `KodiSyncPlugin.cs` — `IMediaSyncPlugin` with movie + TV + music sync
9. Git init, push to `thegoddamnbeckster/Chronicle.Plugin.Kodi`

### Phase 2 (Polish)
- Scheduled sync (configurable interval via Quartz.NET or `IHostedService`)
- Post-scrobble auto-sync (subscribe to scrobble events)
- NFO file generation for unmatched items
- Frontend: Sync button on Plugins page with live progress
- Sync history log (last sync time, item counts, errors)

---

## Chronicle Plugin Files

### `KodiModels.cs`

```csharp
// JSON-RPC envelope
public record JsonRpcRequest(string Method, object? Params = null, int Id = 1);
public record JsonRpcResponse<T>(T? Result, JsonRpcError? Error, int Id);
public record JsonRpcError(int Code, string Message);

// VideoLibrary.GetMovies response
public record KodiMovie(int MovieId, string Title, int Year,
    string ImdBNumber, int UserRating, int PlayCount, string LastPlayed,
    Dictionary<string, string> UniqueId);

// VideoLibrary.GetTVShows response
public record KodiTvShow(int TvShowId, string Title, int Year,
    string ImdBNumber, int UserRating, int PlayCount,
    Dictionary<string, string> UniqueId);

// VideoLibrary.GetEpisodes response
public record KodiEpisode(int EpisodeId, string Title, int Season, int Episode,
    int TvShowId, int UserRating, int PlayCount, string LastPlayed);

// MusicLibrary.GetAlbums response
public record KodiAlbum(int AlbumId, string Title, List<string> Artist,
    int Year, int UserRating, int PlayCount);

// MusicLibrary.GetSongs response
public record KodiSong(int SongId, string Title, List<string> Artist,
    string Album, int Year, int UserRating, int PlayCount, string LastPlayed);
```

### `KodiClient.cs` (Key Methods)

```csharp
public class KodiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public KodiClient(string host, int port, string? username, string? password)
    {
        _baseUrl = $"http://{host}:{port}/jsonrpc";
        _http = new HttpClient();
        if (!string.IsNullOrEmpty(username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{username}:{password}"));
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<bool> PingAsync(CancellationToken ct = default);
    public Task<List<KodiMovie>> GetMoviesAsync(CancellationToken ct = default);
    public Task SetMovieDetailsAsync(int movieId, int? rating, int? playCount,
        DateTimeOffset? lastPlayed, CancellationToken ct = default);
    public Task<List<KodiTvShow>> GetTvShowsAsync(CancellationToken ct = default);
    public Task SetTvShowDetailsAsync(int tvShowId, int? rating,
        CancellationToken ct = default);
    public Task<List<KodiEpisode>> GetEpisodesAsync(int tvShowId,
        CancellationToken ct = default);
    public Task SetEpisodeDetailsAsync(int episodeId, int? rating, int? playCount,
        DateTimeOffset? lastPlayed, CancellationToken ct = default);
    public Task<List<KodiAlbum>> GetAlbumsAsync(CancellationToken ct = default);
    public Task SetAlbumDetailsAsync(int albumId, int? rating,
        CancellationToken ct = default);
    public Task<List<KodiSong>> GetSongsAsync(CancellationToken ct = default);
    public Task SetSongDetailsAsync(int songId, int? rating, int? playCount,
        DateTimeOffset? lastPlayed, CancellationToken ct = default);
}
```

---

## Kodi Version Compatibility

The JSON-RPC API is versioned. Chronicle targets **Kodi 19 "Matrix"** and later (JSON-RPC API v12+). The methods and properties used in this design have been stable since Kodi 17 "Krypton".

Version detection at startup:
```
JSONRPC.Version → { "major": 12, "minor": 3, "patch": 0 }
```
Chronicle logs the Kodi version and aborts the sync with a friendly error if the API is too old.

---

## Security Considerations

- Kodi JSON-RPC credentials are stored in Chronicle's encrypted plugin settings (same as API keys for other plugins)
- The plugin communicates over plain HTTP (Kodi does not support HTTPS on its built-in web server); this is acceptable for local network use only
- Users should NOT expose Kodi's web interface to the public internet
- Chronicle documents this in the plugin settings description

---

## Plugin Repository

**Target:** `thegoddamnbeckster/Chronicle.Plugin.Kodi`
**manifest.json iconUrl:** `https://kodi.tv/favicon.ico`
