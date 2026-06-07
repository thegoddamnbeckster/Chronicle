# Chronicle.Plugin.TheTVDB — Design Document

**Plugin ID:** `chronicle.plugin.thetvdb`
**Repo:** `thegoddamnbeckster/Chronicle.Plugin.TheTVDB`
**Local path:** `W:\Scripts\Chronicle.Plugin.TheTVDB\`
**Version:** 1.0.0
**Author:** Chronicle Contributors
**API:** TheTVDB REST API v4 — `https://api4.thetvdb.com/v4`
**Brand colour (light):** `#6AB7E2`
**Brand colour (dark):** `#3C8DBE`

---

## Purpose

TheTVDB is the community standard for TV series identification. Sonarr, Plex, Kodi,
Trakt, and SIMKL all cross-reference by TVDB series ID — making TVDB IDs the
"canonical" TV identifier in Chronicle's ecosystem. Trakt and SIMKL sync already
stores TVDB IDs in `media_external_ids` (source `"tvdb"`); this plugin reads those
IDs and enriches items with TVDB metadata.

TVDB fills gaps that TMDB leaves: community-contributed episode summaries for older
shows, more complete season/episode air dates, and network/status information.
For anime it also provides absolute episode ordering, though the main enrichment
engine uses title-matching as the fallback when numbering systems diverge.

---

## What TMDB Already Covers (Don't Duplicate)

| Field | TMDB handles it | TVDB also has it | Who wins |
|-------|-----------------|------------------|----------|
| title, overview, year | ✓ | ✓ | Metadata Assignment (user-configurable) |
| poster_url, backdrop_url | ✓ | ✓ | Metadata Assignment |
| genres, cast, directors | ✓ | ✓ | Metadata Assignment |
| rating | ✓ | ✓ | Metadata Assignment |
| banner_url | — | ✓ | TVDB |
| network, status | partial | ✓ | TVDB |
| imdb_id, zap2it_id | imdb only | ✓ | TVDB (extra cross-refs) |
| Movie metadata | ✓ | — | TMDB (TVDB is TV-only) |

TVDB should be installed at lower priority than TMDB for fields both cover, so
TMDB wins by default. Users can promote TVDB on a per-field basis via Metadata Assignment.

---

## Supported Media Types

```csharp
new MediaTypeSupport
{
    MediaTypeName    = "tv",
    DisplayName      = "TV",
    HierarchyLevels  = 3,
    HierarchyLabels  = ["Show", "Season", "Episode"],
    DefaultPriority  = 15,   // below TMDB (10), above Fanart.tv (20)
    SupportedFields  = ["title", "overview", "year", "poster_url", "backdrop_url",
                        "banner_url", "genres", "cast", "directors", "rating", "tags"],
    LevelFields = new Dictionary<int, List<string>>
    {
        [1] = ["title", "overview", "year", "poster_url", "banner_url"],
        [2] = ["title", "overview", "year", "runtime_minutes", "rating", "directors", "cast"],
    },
},
new MediaTypeSupport
{
    MediaTypeName    = "anime",
    DisplayName      = "Anime",
    HierarchyLevels  = 3,
    HierarchyLabels  = ["Show", "Season", "Episode"],
    DefaultPriority  = 15,
    SupportedFields  = ["title", "overview", "year", "poster_url", "backdrop_url",
                        "banner_url", "genres", "cast", "directors", "rating", "tags"],
    LevelFields = new Dictionary<int, List<string>>
    {
        [1] = ["title", "overview", "year", "poster_url", "banner_url"],
        [2] = ["title", "overview", "year", "runtime_minutes", "rating", "directors", "cast"],
    },
},
```

---

## API Overview

**Base URL:** `https://api4.thetvdb.com/v4`

**Authentication:** POST `/login` with `{"apikey": "..."}` → short-lived JWT.
Include the JWT in subsequent requests as `Authorization: Bearer {token}`.
Token lifetime: 30 days. Refresh proactively at day 25 (or on 401 response).

| Operation | Endpoint | Notes |
|-----------|----------|-------|
| Login | `POST /login` | Returns JWT |
| Search series | `GET /search?query={q}&type=series` | Returns candidate list |
| Series extended | `GET /series/{id}/extended?meta=translations` | Full series with translations |
| Series artwork | `GET /series/{id}/artworks` | All artwork for a series |
| Seasons | `GET /series/{id}/seasons/official/extended` | Season list (official/aired order) |
| Season extended | `GET /seasons/{id}/extended?meta=translations` | Single season with episodes |
| Episodes for season | `GET /series/{id}/episodes/official?season={n}&page={p}` | Paginated |
| Episode extended | `GET /episodes/{id}/extended?meta=translations` | Full episode detail |
| Translations | `GET /series/{id}/translations/{lang}` | Per-language override |

---

## External ID Format

**Source stored in `media_external_ids`:** `"thetvdb"`

| Item type | ExternalId format | Example |
|-----------|------------------|---------|
| TV Show | `series:{tvdbSeriesId}` | `series:76290` |
| TV Season | `series:{tvdbSeriesId}/season:{n}` | `series:76290/season:2` |
| TV Episode | `episode:{tvdbEpisodeId}` | `episode:308662` |

**Reading cross-reference IDs from other plugins:**

| Source key | Stored by | Format | Used for |
|-----------|-----------|--------|---------|
| `"tvdb"` | Trakt, SIMKL | raw numeric string `"76290"` | look up series without a TVDB search |
| `"parent_tvdb"` | Chronicle enrichment injector | raw numeric string | seasons/episodes inherit series TVDB ID |
| `"thetvdb"` | This plugin | `series:{id}` / `episode:{id}` | own stored ID |
| `"tmdb"` | TMDB plugin | `"tv:{tmdbId}"` | cross-check titles, not primary |

Trakt and SIMKL already store raw TVDB numeric IDs with source `"tvdb"` when syncing.
The TVDB plugin reads `KnownExternalIds["tvdb"]` (raw number from Trakt/SIMKL) and
`KnownExternalIds["thetvdb"]` (its own stored format) in that order of preference.

---

## Settings Schema

| Key | Label | Type | Required | Default | Notes |
|-----|-------|------|----------|---------|-------|
| `api_key` | TheTVDB API Key | Password | Yes | — | Free at thetvdb.com/dashboard |
| `language` | Preferred Language | Text | No | `eng` | ISO 639-2 (3-letter): eng, fra, deu, jpn |
| `fallback_language` | Fallback Language | Text | No | `eng` | Used when preferred lang has no translation |

Note: TVDB uses **3-letter ISO 639-2** codes (not 2-letter ISO 639-1 like TMDB/Fanart.tv).

---

## Background Tasks (manifest.json)

```json
"background_tasks": [
  {
    "task_id":         "fetch-missing-metadata",
    "display_name":    "Fetch Missing TV Metadata",
    "description":     "Fetches metadata from TheTVDB for TV items that have a TVDB ID (from Trakt or SIMKL sync) but have not yet been enriched by this plugin.",
    "default_cron":    "0 4 * * *",
    "default_enabled": true
  },
  {
    "task_id":         "resync-all-metadata",
    "display_name":    "Re-sync All TV Metadata",
    "description":     "Re-fetches TheTVDB metadata for all enriched TV items to pick up community edits and new episode data.",
    "default_cron":    "0 2 * * 0",
    "default_enabled": false
  }
]
```

---

## Fix Match Hint

```json
"fixMatchHint": "Enter a TheTVDB series ID (76290), a TheTVDB series URL (https://thetvdb.com/series/breaking-bad), or for episodes enter the episode ID directly (episode:308662). Series slugs in URLs are resolved automatically."
```

**GetByIdAsync** must accept:
- Raw numeric: `"76290"` → treated as `series:76290`
- Prefixed: `"series:76290"`, `"episode:308662"`, `"series:76290/season:2"`
- TVDB URL: `"https://thetvdb.com/series/breaking-bad"` → slug lookup via search
- TVDB URL: `"https://thetvdb.com/series/76290"` → direct numeric extraction

---

## File Structure

```
W:\Scripts\Chronicle.Plugin.TheTVDB\
├── Chronicle.Plugin.TheTVDB.csproj
├── manifest.json
├── AssemblyInfo.cs
├── TheTvdbMetadataProvider.cs   ← main IMetadataProvider
├── TheTvdbClient.cs             ← HTTP wrapper + token management
├── TheTvdbModels.cs             ← API response records
└── README.md                    ← user-facing plugin authoring guide (not this file)
```

---

## Token Management (TheTvdbClient)

The TVDB v4 API requires a JWT obtained from `/login`. Tokens live 30 days. Design:

```csharp
private string? _token;
private DateTimeOffset _tokenExpiresAt;
private readonly SemaphoreSlim _tokenLock = new(1, 1);
private const int TokenLifetimeDays = 30;
private const int RefreshBeforeDays = 5;   // refresh at day 25

private async Task<string> GetTokenAsync(CancellationToken ct)
{
    if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
        return _token;

    await _tokenLock.WaitAsync(ct);
    try
    {
        // Double-check after acquiring lock
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            return _token;

        var response = await _http.PostAsJsonAsync($"{BaseUrl}/login",
            new { apikey = _apiKey }, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TvdbLoginResponse>(ct: ct);
        _token = body!.Data.Token;
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddDays(TokenLifetimeDays - RefreshBeforeDays);
        return _token;
    }
    finally { _tokenLock.Release(); }
}
```

Every request calls `GetTokenAsync()` first, then adds `Authorization: Bearer {token}`.
On 401 response, clear `_token` and retry once (handles revoked tokens).

---

## SearchAsync Strategy by Level

### Level 0 — TV Show

1. Check `KnownExternalIds["tvdb"]` (raw numeric from Trakt/SIMKL) → direct `GET /series/{id}/extended`
2. Check `KnownExternalIds["thetvdb"]` (own `"series:{id}"` format) → extract ID → same call
3. If no known ID: text search `GET /search?query={Name}&type=series` → score candidates:
   - Exact name match → +60
   - Year match (±1) → +30
   - Language preference → +10
   - Return top candidate if score ≥ 50

### Level 1 — Season

1. Derive parent TVDB series ID from `KnownExternalIds["tvdb"]` or `KnownExternalIds["parent_tvdb"]` or `KnownExternalIds["thetvdb"]`
2. If no parent ID → return empty (cannot look up season without series ID)
3. Call `GET /series/{seriesId}/seasons/official/extended`
4. Filter by `SeasonNumber == context.ItemNumber`
5. If found: call `GET /seasons/{seasonId}/extended?meta=translations` for full data

### Level 2 — Episode

1. Derive parent TVDB series ID (same chain as Level 1)
2. If no parent ID → return empty
3. Call `GET /series/{seriesId}/episodes/official?season={parentSeasonNumber}` (paginated — handle `page` parameter)
4. Match by `EpisodeNumber == context.ItemNumber` (within correct season)
5. **If no match found by number** (numbering mismatch — TVDB vs TMDB ordering):
   - Fall back to title matching: find episode where `Name` contains or closely matches `context.Name` (case-insensitive, strip punctuation)
   - Log a warning: `"TVDB episode S{s}E{e} not found for '{name}' — fell back to title match"`
6. If found: call `GET /episodes/{episodeId}/extended?meta=translations` for full data

**Episode numbering note:** Sonarr, Trakt, and most scrapers use TVDB's "Aired Order" numbering.
TMDB uses its own numbering. For most shows these match; for anime they often diverge
(TVDB uses absolute numbering). Chronicle stores what TVDB says; users can adjust via
Metadata Assignment to prefer TMDB overrides for the title/number fields.

---

## GetByIdAsync Supported Formats

```
"76290"                         → series:76290 (bare numeric treated as series)
"series:76290"                  → GET /series/76290/extended
"series:76290/season:2"         → GET /series/76290/seasons/official/extended, filter season 2
"episode:308662"                → GET /episodes/308662/extended
"https://thetvdb.com/series/breaking-bad"     → slug search
"https://www.thetvdb.com/series/76290"        → numeric extraction
```

---

## MediaMetadata Mapping

### Show (level 0) — from `/series/{id}/extended`

```csharp
new MediaMetadata
{
    ExternalId    = $"series:{series.Id}",
    Source        = "thetvdb",
    Title         = translation?.Name ?? series.Name,
    Overview      = translation?.Overview ?? series.Overview,
    Year          = series.FirstAired?.Year,
    PosterUrl     = BestArtwork(series.Artworks, "poster"),
    BackdropUrl   = BestArtwork(series.Artworks, "background"),
    BannerUrl     = BestArtwork(series.Artworks, "banner"),
    Genres        = series.Genres?.Select(g => g.Name).ToList(),
    Cast          = MapCast(series.Characters),
    Directors     = null,     // not at series level
    Rating        = series.Score,
    AdditionalIds = new Dictionary<string, string?>
    {
        ["imdb"]    = series.RemoteIds?.FirstOrDefault(r => r.SourceName == "IMDB")?.Id,
        ["zap2it"]  = series.RemoteIds?.FirstOrDefault(r => r.SourceName == "Zap2It")?.Id,
    }.Where(kv => kv.Value != null)
     .ToDictionary(kv => kv.Key, kv => kv.Value!),
    AdditionalData = new Dictionary<string, object?>
    {
        ["network"]       = series.LatestNetwork?.Name,
        ["status"]        = series.Status?.Name,
        ["originalCountry"] = series.OriginalCountry,
        ["averageRuntime"]  = series.AverageRuntime,
    },
}
```

### Season (level 1) — from `/seasons/{id}/extended`

```csharp
new MediaMetadata
{
    ExternalId = $"series:{seriesId}/season:{season.Number}",
    Source     = "thetvdb",
    Title      = translation?.Name ?? $"Season {season.Number}",
    Overview   = translation?.Overview ?? season.Overview,
    Year       = season.Year,
    PosterUrl  = BestArtwork(season.Artwork, "season"),
    BannerUrl  = BestArtwork(season.Artwork, "seasonbanner"),
}
```

### Episode (level 2) — from `/episodes/{id}/extended`

```csharp
new MediaMetadata
{
    ExternalId      = $"episode:{episode.Id}",
    Source          = "thetvdb",
    Title           = translation?.Name ?? episode.Name,
    Overview        = translation?.Overview ?? episode.Overview,
    Year            = episode.Aired?.Year,
    RuntimeMinutes  = episode.Runtime,
    Rating          = episode.Score,
    Directors       = episode.Characters
                        ?.Where(c => c.Type == CharacterType.Director)
                         .Select(c => new MediaCredit { Role = "Director", PersonName = c.PersonName })
                         .ToList(),
    Cast            = episode.Characters
                        ?.Where(c => c.Type == CharacterType.Actor)
                         .Select(c => new MediaCredit
                         {
                             Role       = "Actor",
                             PersonName = c.PersonName,
                             Character  = c.Name,
                         })
                         .ToList(),
}
```

**`BestArtwork()`** — filter artworks by type, then prefer the configured language, then
fall back to English, then take the highest-scored image.

---

## TheTvdbModels.cs (response records)

Key records needed (all deserialised with `JsonPropertyName`):

```csharp
record TvdbLoginResponse(TvdbLoginData Data);
record TvdbLoginData(string Token);

record TvdbResponse<T>(T Data, string? Status);
record TvdbPagedResponse<T>(T[] Data, TvdbLinks Links);
record TvdbLinks(string? Prev, string? Next, string? Self, int TotalItems, int PageSize);

record TvdbSeries(long Id, string Name, string? Slug, string? Overview,
    string? FirstAired, float? Score, string? OriginalCountry, string? OriginalLanguage,
    int? AverageRuntime, TvdbStatus? Status, TvdbNetwork? LatestNetwork,
    TvdbGenre[]? Genres, TvdbArtwork[]? Artworks, TvdbCharacter[]? Characters,
    TvdbRemoteId[]? RemoteIds);

record TvdbSeason(long Id, int Number, int? Year, string? Overview,
    TvdbArtwork[]? Artwork, TvdbEpisode[]? Episodes);

record TvdbEpisode(long Id, int? SeasonNumber, int? Number, string? Name,
    string? Overview, string? Aired, int? Runtime, float? Score,
    TvdbCharacter[]? Characters);

record TvdbArtwork(long Id, string? Image, string? Thumbnail,
    string? Language, string Type, int? Score);

record TvdbCharacter(long Id, string? Name, string? PersonName, int Type);
    // Type: 3=Actor, 4=Director, 7=Writer, etc.

record TvdbGenre(long Id, string Name);
record TvdbStatus(string Name);
record TvdbNetwork(string Name);
record TvdbRemoteId(string Id, string SourceName);
record TvdbTranslation(string? Name, string? Overview, string Language);
```

---

## TheTvdbClient.cs — Public API surface

```csharp
internal sealed class TheTvdbClient : IDisposable
{
    Task<TvdbSeries?>    SearchSeriesAsync(string query, CancellationToken ct);
    Task<TvdbSeries?>    GetSeriesExtendedAsync(long seriesId, string language, CancellationToken ct);
    Task<TvdbSeason[]?>  GetSeasonsAsync(long seriesId, CancellationToken ct);
    Task<TvdbSeason?>    GetSeasonExtendedAsync(long seasonId, string language, CancellationToken ct);
    Task<TvdbEpisode[]?> GetEpisodesForSeasonAsync(long seriesId, int seasonNumber, CancellationToken ct);
    Task<TvdbEpisode?>   GetEpisodeExtendedAsync(long episodeId, string language, CancellationToken ct);
    Task<long?>          ResolveSlugAsync(string slug, CancellationToken ct);   // for Fix Match URLs
    Task<bool>           HealthCheckAsync(CancellationToken ct);
}
```

`GetEpisodesForSeasonAsync` must handle TVDB's pagination (100 episodes per page, `?page=0`, `?page=1`, etc.). Accumulate all pages.

---

## Artwork Selection Logic

TVDB artwork types used:
- `"poster"` → PosterUrl (show/season)
- `"background"` → BackdropUrl (show)
- `"banner"` → BannerUrl (show/season)
- `"seasonposter"` → PosterUrl (season)
- `"seasonbanner"` → BannerUrl (season)

Selection priority:
1. `Language == configured language` → highest score first
2. `Language == "eng"` (or null) → highest score first
3. Any remaining → highest score first

---

## Rate Limits and Resilience

- Free tier: no published hard limit for reasonable use; avoid bulk-fetching more than ~50 items/minute
- Add `await Task.Delay(TimeSpan.FromMilliseconds(200))` between paginated episode fetches
- Respect `Retry-After` header on 429
- Token 401: clear `_token`, refresh once, retry the failed request
- Network errors: log and return `null` (enrichment marks row as `Error` status, retried later)

---

## Integration with Chronicle Enrichment Pipeline

The enrichment pipeline already:
1. Injects `KnownExternalIds` containing all `media_external_ids` for an item, plus parent IDs prefixed with `parent_`
2. Calls `SearchAsync(context)` → top candidate → `GetByIdAsync(externalId)` → stores result
3. Stores the returned `ExternalId` back into `media_external_ids` with source `"thetvdb"`

Because Trakt/SIMKL already populate `media_external_ids` with source `"tvdb"` (raw numeric),
TVDB items will already have `KnownExternalIds["tvdb"] = "76290"` available at enrichment time.
The plugin reads this, bypasses the text search, and goes straight to `GET /series/{id}/extended`.

The plugin should also write `AdditionalIds["imdb"]` and `AdditionalIds["zap2it"]` into
the returned `MediaMetadata` — the enrichment service will store these as additional
`media_external_ids` rows, allowing other plugins (Fanart.tv already reads TVDB IDs) to
cross-reference them.

---

## Episode Numbering Mismatch (TVDB vs TMDB)

The most common failure mode: a user's library items were numbered by TMDB (e.g. TMDB says
"The Office S01E01") but TVDB uses different season/episode numbers. This affects:
- Anime with absolute ordering (Trakt reports S01E023 but TVDB stores it as S01E023 in Aired Order — usually matches, but specials differ)
- Shows with differently-counted specials (Season 0 episodes)
- Some UK shows (TVDB and TMDB split seasons differently)

**Mitigation already built into the design:**
- Episode SearchAsync tries S+E number match first, then title match fallback
- Log the fallback so users can see when it fires in the enrichment drill-down
- Users can promote TMDB over TVDB for episode title/number via Metadata Assignment if they prefer TMDB ordering
- Future work: add a "Prefer Absolute Ordering" setting for anime

---

## csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>Chronicle.Plugin.TheTVDB</AssemblyName>
    <RootNamespace>Chronicle.Plugin.TheTVDB</RootNamespace>
    <Version>1.0.0</Version>
  </PropertyGroup>

  <ItemGroup>
    <!-- Chronicle.Plugins.dll is provided by the host at runtime — do NOT copy to output -->
    <ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                      Private="false"
                      ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
    <!-- manifest.json must be in the plugin output directory alongside the DLL -->
    <None Update="manifest.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

---

## manifest.json

```json
{
  "plugin_id":   "chronicle.plugin.thetvdb",
  "name":        "TheTVDB",
  "version":     "1.0.0",
  "author":      "Chronicle Contributors",
  "description": "Metadata for TV series, seasons, and episodes from TheTVDB — the community standard for TV identification used by Sonarr, Plex, Kodi, Trakt, and SIMKL.",
  "min_chronicle_version": "0.6.0",
  "entry_type":  "Chronicle.Plugin.TheTVDB.TheTvdbMetadataProvider",
  "iconUrl":     "https://thetvdb.com/favicon.ico",
  "brandColorLight": "#6AB7E2",
  "brandColorDark":  "#3C8DBE",
  "fixMatchHint": "Enter a TheTVDB series ID (76290), a series URL (https://thetvdb.com/series/breaking-bad), or for a specific episode use episode:{episodeId} (episode:308662). Series slugs in URLs are resolved automatically.",
  "background_tasks": [
    {
      "task_id":         "fetch-missing-metadata",
      "display_name":    "Fetch Missing TV Metadata",
      "description":     "Fetches TheTVDB metadata for TV items that have a TVDB ID stored (from Trakt or SIMKL sync) but have not yet been enriched by TheTVDB.",
      "default_cron":    "0 4 * * *",
      "default_enabled": true
    },
    {
      "task_id":         "resync-all-metadata",
      "display_name":    "Re-sync All TV Metadata",
      "description":     "Re-fetches all TheTVDB metadata to pick up community edits, new episodes, and artwork changes.",
      "default_cron":    "0 2 * * 0",
      "default_enabled": false
    }
  ],
  "settings": [
    {
      "key":      "api_key",
      "label":    "TheTVDB API Key",
      "type":     "password",
      "required": true,
      "description": "Your TheTVDB API key. Get one free at https://thetvdb.com/dashboard."
    },
    {
      "key":          "language",
      "label":        "Preferred Language",
      "type":         "text",
      "required":     false,
      "default":      "eng",
      "description":  "ISO 639-2 three-letter language code (e.g. eng, fra, deu, jpn). Translations in this language are preferred when available."
    },
    {
      "key":          "fallback_language",
      "label":        "Fallback Language",
      "type":         "text",
      "required":     false,
      "default":      "eng",
      "description":  "Language used when no translation exists in the preferred language."
    }
  ]
}
```

---

## RunTestEnvironment.ps1 Entry

Add to `$PluginProjects` array in `scripts/RunTestEnvironment.ps1`:

```powershell
@{ Name = "TheTVDB";        Path = "W:\Scripts\Chronicle.Plugin.TheTVDB";        Id = "chronicle.plugin.thetvdb" },
```

---

## GitHub Repository Setup

1. Create repo: `thegoddamnbeckster/Chronicle.Plugin.TheTVDB`
2. Create initial release `v1.0.0` after first working build
3. Add release badge to README: `[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.TheTVDB?style=flat-square&label=release)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TheTVDB/releases/latest)`

---

## Things That Can Go Wrong (Lessons from Fanart.tv)

| Problem | Prevention |
|---------|-----------|
| Static field init order | Declare `AllSupportedTypes` array **after** all individual `MediaTypeSupport` static fields |
| `ILogger` ambiguous reference | `using ILogger = Serilog.ILogger;` at top of provider file |
| Duplicate external IDs from Trakt/SIMKL | Enrichment service now deduplicates — TVDB plugin doesn't need to worry about this |
| 401 on a valid token (race condition) | Double-check inside `_tokenLock` before refreshing |
| Pagination not handled | `GetEpisodesForSeasonAsync` must loop until `Links.Next` is null |
| TVDB artwork URLs sometimes have no protocol | Prefix `https:` if URL starts with `//` |
| Episode number mismatch logged as error | Log at Warning level, not Error — it's an expected fallback |
| GetSupportedMediaTypes called on every enrichment | Method is called frequently; never do I/O inside it |

---

## Implementation Order

1. `TheTvdbModels.cs` — all response records (no logic, easy to write and test)
2. `TheTvdbClient.cs` — HTTP + token management, test with a real API key
3. `TheTvdbMetadataProvider.cs` — start with Show-level search/fetch, then Season, then Episode
4. `manifest.json` + `Chronicle.Plugin.TheTVDB.csproj`
5. Build, deploy to `plugins/chronicle.plugin.thetvdb/`, test end-to-end in RunTestEnvironment
6. Add to RunTestEnvironment.ps1
7. Update SettingsController.TypeParentMap if anime needs TVDB TV parent entries (probably not needed — anime is a separate DB type, not a child of tv)
8. Create GitHub repo, initial commit, v1.0.0 release
