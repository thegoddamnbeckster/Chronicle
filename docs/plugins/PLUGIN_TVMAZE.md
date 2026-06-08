# Chronicle.Plugin.TVMaze — Design Document

**Plugin ID:** `chronicle.plugin.tvmaze`
**Repo:** `thegoddamnbeckster/Chronicle.Plugin.TVMaze`
**Local path:** `W:\Scripts\Chronicle.Plugin.TVMaze\`
**Version:** 1.0.0
**Author:** Chronicle Contributors
**API:** TVMaze REST API — `https://api.tvmaze.com`
**Auth:** None — no API key, no signup, no configuration required
**Brand colour (light):** `#CF0000`
**Brand colour (dark):** `#FF2929`

---

## Purpose

TVMaze is a community TV database with a completely open public API — no
registration, no API key, no project description form. It covers TV shows,
seasons, episodes, cast, crew, and artwork for mainstream and niche content,
and is the recommended default TV metadata source for Chronicle users who
don't want to deal with the TVDB API signup process.

TVMaze stores TVDB IDs in every show response (`externals.thetvdb`), and
exposes a direct `GET /lookup/shows?thetvdb={id}` endpoint — so items that
Trakt or SIMKL have already given a TVDB ID still resolve without a text
search. This makes TVMaze a near-seamless replacement for TheTVDB from the
enrichment pipeline's perspective.

---

## What TMDB Already Covers (Don't Duplicate)

| Field | TMDB handles it | TVMaze also has it | Who wins |
|-------|-----------------|-------------------|----------|
| title, overview, year | ✓ | ✓ | Metadata Assignment |
| poster_url, backdrop_url | ✓ | ✓ | Metadata Assignment |
| genres, cast, directors | ✓ | ✓ | Metadata Assignment |
| rating | ✓ | ✓ | Metadata Assignment |
| banner_url | — | ✓ | TVMaze |
| network, status | partial | ✓ | TVMaze |
| imdb_id, thetvdb cross-ref | imdb only | ✓ | TVMaze (writes tvdb back) |
| Movie metadata | ✓ | — | TMDB (TVMaze is TV-only) |

TVMaze should be installed at lower priority than TMDB for fields both cover,
so TMDB wins by default. Users can promote TVMaze on a per-field basis.

---

## Supported Media Types

```csharp
new MediaTypeSupport
{
    MediaTypeName    = "tv",
    DisplayName      = "TV",
    HierarchyLevels  = 3,
    HierarchyLabels  = ["Show", "Season", "Episode"],
    DefaultPriority  = 15,   // same slot as TheTVDB; user chooses one or both
    SupportedFields  = ["title", "overview", "year", "poster_url", "backdrop_url",
                        "banner_url", "genres", "cast", "directors", "rating", "tags"],
    LevelFields = new Dictionary<int, List<string>>
    {
        [1] = ["title", "overview", "year", "poster_url"],
        [2] = ["title", "overview", "year", "runtime_minutes", "rating", "cast"],
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
        [1] = ["title", "overview", "year", "poster_url"],
        [2] = ["title", "overview", "year", "runtime_minutes", "rating", "cast"],
    },
},
```

---

## API Overview

**Base URL:** `https://api.tvmaze.com`
**Authentication:** None.
**Rate limit:** 20 requests per 10 seconds for unregistered clients. A simple
per-second token bucket (100ms minimum delay between requests) keeps Chronicle
well within limits.

| Operation | Endpoint | Notes |
|-----------|----------|-------|
| Text search | `GET /search/shows?q={query}` | Returns scored candidates |
| Lookup by TVDB ID | `GET /lookup/shows?thetvdb={id}` | Zero-search fast path |
| Lookup by IMDB ID | `GET /lookup/shows?imdb={tt...}` | Alternate cross-ref |
| Show + embeds | `GET /shows/{id}?embed[]=cast&embed[]=images` | Single call for full data |
| Seasons | `GET /shows/{id}/seasons` | All seasons with metadata |
| Episodes for season | `GET /seasons/{seasonId}/episodes` | Flat list for one season |
| Single episode | `GET /episodes/{id}` | Full episode detail |
| Show images | `GET /shows/{id}/images` | Typed artwork list |

The `embed[]` query parameter lets a single show request return cast and images
inline, reducing round-trips for show-level enrichment to one call.

---

## External ID Format

**Source stored in `media_external_ids`:** `"tvmaze"`

| Item type | ExternalId format | Example |
|-----------|------------------|---------|
| TV Show | `show:{tvmazeId}` | `show:169` |
| TV Season | `show:{tvmazeId}/season:{n}` | `show:169/season:2` |
| TV Episode | `episode:{tvmazeId}` | `episode:4952` |

**Reading cross-reference IDs from KnownExternalIds:**

| Source key | Stored by | Format | Used for |
|-----------|-----------|--------|---------|
| `"tvdb"` | Trakt, SIMKL | raw numeric `"76290"` | `/lookup/shows?thetvdb={id}` |
| `"imdb"` | TMDB, Trakt | `"tt0903747"` | `/lookup/shows?imdb={id}` |
| `"tvmaze"` | This plugin | `show:{id}` / `episode:{id}` | own stored ID |
| `"parent_tvmaze"` | Enrichment pipeline | `show:{id}` | season/episode parent lookup |
| `"parent_tvdb"` | Enrichment pipeline | raw numeric | parent TVDB → TVMaze lookup |

The TVMaze `externals.thetvdb` field is written back into `AdditionalIds` so
Fanart.tv can find it even if TheTVDB plugin is not installed.

---

## Settings Schema

**None required.** The plugin works with zero configuration.

There are no optional settings either — language preference is not meaningful
since TVMaze's primary content is English and translation coverage is minimal.

This is the core user experience advantage over TheTVDB.

---

## Background Tasks (manifest.json)

```json
"background_tasks": [
  {
    "task_id":         "fetch-missing-metadata",
    "display_name":    "Fetch Missing TV Metadata",
    "description":     "Fetches TVMaze metadata for TV items that have a TVDB ID (from Trakt or SIMKL sync) or IMDB ID but have not yet been enriched by TVMaze.",
    "default_cron":    "0 4 * * *",
    "default_enabled": true
  },
  {
    "task_id":         "resync-all-metadata",
    "display_name":    "Re-sync All TV Metadata",
    "description":     "Re-fetches all TVMaze metadata to pick up community edits, new episodes, and artwork changes.",
    "default_cron":    "0 2 * * 0",
    "default_enabled": false
  }
]
```

---

## Fix Match Hint

```json
"fixMatchHint": "Enter a TVMaze show ID (169), a TVMaze URL (https://www.tvmaze.com/shows/169/breaking-bad), a TVDB ID prefixed with 'thetvdb:' (thetvdb:76290), or an IMDB ID (tt0903747). TVMaze IDs take precedence; TVDB and IMDB IDs are resolved via lookup."
```

**GetByIdAsync** must accept:
- Bare numeric: `"169"` → treated as `show:169`
- Prefixed: `"show:169"`, `"episode:4952"`, `"show:169/season:2"`
- TVMaze URL: `"https://www.tvmaze.com/shows/169/breaking-bad"` → numeric extraction
- TVDB cross-ref: `"thetvdb:76290"` → `/lookup/shows?thetvdb=76290`
- IMDB cross-ref: `"tt0903747"` or `"imdb:tt0903747"` → `/lookup/shows?imdb=tt0903747`

---

## File Structure

```
W:\Scripts\Chronicle.Plugin.TVMaze\
├── Chronicle.Plugin.TVMaze.csproj
├── manifest.json
├── TvMazeMetadataProvider.cs    ← main IMetadataProvider
├── TvMazeClient.cs              ← HTTP wrapper + rate limiting
├── TvMazeModels.cs              ← API response records
└── README.md
```

No `AssemblyInfo.cs` needed — same as TheTVDB.

---

## Rate Limiting (TvMazeClient)

TVMaze asks for ≤20 requests per 10 seconds. A conservative 100ms inter-request
delay keeps Chronicle at ≤10/sec with no bursting logic needed:

```csharp
private readonly SemaphoreSlim _rateLimiter = new(1, 1);
private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
{
    await _rateLimiter.WaitAsync(ct);
    try
    {
        var wait = MinInterval - (DateTimeOffset.UtcNow - _lastRequest);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

        var resp = await _http.GetAsync(url, ct);
        _lastRequest = DateTimeOffset.UtcNow;

        // Honour 429 Retry-After if TVMaze sends one
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
            await Task.Delay(retry, ct);
            resp = await _http.GetAsync(url, ct);
            _lastRequest = DateTimeOffset.UtcNow;
        }

        return resp;
    }
    finally { _rateLimiter.Release(); }
}
```

No token refresh needed. No auth headers needed. Much simpler than TheTVDB.

---

## SearchAsync Strategy by Level

### Level 0 — TV Show

1. Check `KnownExternalIds["tvdb"]` or `["parent_tvdb"]` → `GET /lookup/shows?thetvdb={id}`
2. Check `KnownExternalIds["imdb"]` → `GET /lookup/shows?imdb={id}`
3. Check `KnownExternalIds["tvmaze"]` → extract show ID → `GET /shows/{id}?embed[]=cast&embed[]=images`
4. Text search: `GET /search/shows?q={Name}` → score candidates by name + year

### Level 1 — Season

1. Resolve parent TVMaze show ID (from own stored `"tvmaze"` key, or via TVDB/IMDB lookup as above)
2. `GET /shows/{showId}/seasons` → filter by `Number == context.ItemNumber`
3. If found → `GET /seasons/{seasonId}/episodes` for episode count only (no full fetch needed at season level)

### Level 2 — Episode

1. Resolve parent TVMaze show ID (same chain)
2. Derive season number from `KnownExternalIds["parent_tvmaze"]` (`show:{id}/season:{n}`)
3. `GET /seasons/{seasonId}/episodes` — match by `Number == context.ItemNumber`
4. **Title fallback** if number doesn't match (TVDB/TVMaze numbering can diverge for specials):
   - Normalise both names (lowercase, strip punctuation) → substring match
   - Log Warning: numbering mismatch, title fallback used
5. `GET /episodes/{episodeId}` for full detail (cast, etc.)

---

## MediaMetadata Mapping

### HTML summary stripping

TVMaze returns `summary` fields as HTML (e.g. `<p>Breaking Bad follows...</p>`). Strip
tags before storing:

```csharp
private static string? StripHtml(string? html)
{
    if (string.IsNullOrWhiteSpace(html)) return null;
    return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "").Trim();
}
```

### Show (level 0) — from `GET /shows/{id}?embed[]=cast&embed[]=images`

```csharp
new MediaMetadata
{
    ExternalId   = $"show:{show.Id}",
    Source       = "tvmaze",
    Title        = show.Name,
    Overview     = StripHtml(show.Summary),
    Year         = ParseYear(show.Premiered),
    PosterUrl    = show.Image?.Original ?? show.Image?.Medium,
    BackdropUrl  = BestImage(show.EmbeddedImages, "background"),
    BannerUrl    = BestImage(show.EmbeddedImages, "banner"),
    Genres       = show.Genres?.ToList() ?? [],
    Cast         = show.EmbeddedCast?
                       .Select(c => c.Person?.Name)
                       .Where(n => n != null)
                       .Cast<string>()
                       .Take(20)
                       .ToList() ?? [],
    Directors    = [],    // TVMaze has no show-level director concept
    Rating       = show.Rating?.Average,
    AdditionalIds = BuildAdditionalIds(show.Externals),
    ExtendedData = BuildExtendedData(show),
}
```

`BuildAdditionalIds` writes `thetvdb` and `imdb` from `show.Externals` so
Fanart.tv can use the TVDB cross-reference even without TheTVDB plugin.

`BuildExtendedData` stores: `network`, `status`, `type`, `language`, `runtime`.

### Season (level 1) — from `GET /shows/{id}/seasons`

```csharp
new MediaMetadata
{
    ExternalId  = $"show:{showId}/season:{season.Number}",
    Source      = "tvmaze",
    Title       = season.Name ?? $"Season {season.Number}",
    Overview    = StripHtml(season.Summary),
    Year        = ParseYear(season.PremiereDate),
    PosterUrl   = season.Image?.Original ?? season.Image?.Medium,
}
```

### Episode (level 2) — from `GET /episodes/{id}`

```csharp
new MediaMetadata
{
    ExternalId     = $"episode:{episode.Id}",
    Source         = "tvmaze",
    Title          = episode.Name ?? string.Empty,
    Overview       = StripHtml(episode.Summary),
    Year           = ParseYear(episode.Airdate),
    RuntimeMinutes = episode.Runtime,
    Rating         = episode.Rating?.Average,
    PosterUrl      = episode.Image?.Original ?? episode.Image?.Medium,
}
```

Note: TVMaze episode responses do not include cast per-episode (only at show level).
`Cast` and `Directors` remain empty at episode level — this is expected and correct.

---

## TvMazeModels.cs (key records)

```csharp
record TvMazeSearchResult(
    [JsonPropertyName("score")]  double     Score,
    [JsonPropertyName("show")]   TvMazeShow Show);

record TvMazeShow(
    [JsonPropertyName("id")]       int          Id,
    [JsonPropertyName("name")]     string       Name,
    [JsonPropertyName("type")]     string?      Type,
    [JsonPropertyName("language")] string?      Language,
    [JsonPropertyName("genres")]   string[]?    Genres,
    [JsonPropertyName("status")]   string?      Status,
    [JsonPropertyName("runtime")]  int?         Runtime,
    [JsonPropertyName("premiered")]string?      Premiered,
    [JsonPropertyName("summary")]  string?      Summary,
    [JsonPropertyName("rating")]   TvMazeRating? Rating,
    [JsonPropertyName("image")]    TvMazeImage?  Image,
    [JsonPropertyName("network")]  TvMazeNetwork? Network,
    [JsonPropertyName("externals")]TvMazeExternals? Externals,
    [JsonPropertyName("_embedded")]TvMazeEmbedded? Embedded);

record TvMazeSeason(
    [JsonPropertyName("id")]           int         Id,
    [JsonPropertyName("number")]       int         Number,
    [JsonPropertyName("name")]         string?     Name,
    [JsonPropertyName("episodeOrder")] int?        EpisodeOrder,
    [JsonPropertyName("premiereDate")] string?     PremiereDate,
    [JsonPropertyName("endDate")]      string?     EndDate,
    [JsonPropertyName("summary")]      string?     Summary,
    [JsonPropertyName("image")]        TvMazeImage? Image);

record TvMazeEpisode(
    [JsonPropertyName("id")]      int          Id,
    [JsonPropertyName("name")]    string?      Name,
    [JsonPropertyName("season")]  int?         Season,
    [JsonPropertyName("number")]  int?         Number,
    [JsonPropertyName("airdate")] string?      Airdate,
    [JsonPropertyName("runtime")] int?         Runtime,
    [JsonPropertyName("summary")] string?      Summary,
    [JsonPropertyName("rating")]  TvMazeRating? Rating,
    [JsonPropertyName("image")]   TvMazeImage?  Image);

record TvMazeImage(
    [JsonPropertyName("medium")]   string? Medium,
    [JsonPropertyName("original")] string? Original);

record TvMazeRating(
    [JsonPropertyName("average")] double? Average);

record TvMazeNetwork(
    [JsonPropertyName("name")]    string Name,
    [JsonPropertyName("country")] TvMazeCountry? Country);

record TvMazeCountry(
    [JsonPropertyName("name")] string? Name,
    [JsonPropertyName("code")] string? Code);

record TvMazeExternals(
    [JsonPropertyName("thetvdb")] long?   TheTvdb,
    [JsonPropertyName("imdb")]    string? Imdb,
    [JsonPropertyName("tvrage")]  long?   TvRage);

record TvMazeCastMember(
    [JsonPropertyName("person")]    TvMazePerson?    Person,
    [JsonPropertyName("character")] TvMazeCharacter? Character);

record TvMazePerson(
    [JsonPropertyName("id")]   int    Id,
    [JsonPropertyName("name")] string Name);

record TvMazeCharacter(
    [JsonPropertyName("id")]   int    Id,
    [JsonPropertyName("name")] string Name);

record TvMazeArtwork(
    [JsonPropertyName("id")]        int        Id,
    [JsonPropertyName("type")]      string     Type,
    [JsonPropertyName("main")]      bool       Main,
    [JsonPropertyName("resolutions")]TvMazeArtworkResolutions? Resolutions);

record TvMazeArtworkResolutions(
    [JsonPropertyName("original")] TvMazeArtworkSize? Original,
    [JsonPropertyName("medium")]   TvMazeArtworkSize? Medium);

record TvMazeArtworkSize(
    [JsonPropertyName("url")]    string? Url,
    [JsonPropertyName("width")]  int?    Width,
    [JsonPropertyName("height")] int?    Height);

record TvMazeEmbedded(
    [JsonPropertyName("cast")]   TvMazeCastMember[]? Cast,
    [JsonPropertyName("images")] TvMazeArtwork[]?    Images);
```

---

## TvMazeClient.cs — Public API Surface

```csharp
internal sealed class TvMazeClient : IDisposable
{
    Task<TvMazeSearchResult[]?> SearchShowsAsync(string query, CancellationToken ct);
    Task<TvMazeShow?>           LookupByTvdbIdAsync(long tvdbId, CancellationToken ct);
    Task<TvMazeShow?>           LookupByImdbIdAsync(string imdbId, CancellationToken ct);
    Task<TvMazeShow?>           GetShowAsync(int showId, CancellationToken ct);
    Task<TvMazeSeason[]?>       GetSeasonsAsync(int showId, CancellationToken ct);
    Task<TvMazeEpisode[]?>      GetEpisodesForSeasonAsync(int seasonId, CancellationToken ct);
    Task<TvMazeEpisode?>        GetEpisodeAsync(int episodeId, CancellationToken ct);
    Task<TvMazeArtwork[]?>      GetArtworkAsync(int showId, CancellationToken ct);
    Task<bool>                  HealthCheckAsync(CancellationToken ct);
}
```

`GetShowAsync` embeds cast and images inline:
`/shows/{id}?embed[]=cast&embed[]=images`

`LookupByTvdbIdAsync` and `LookupByImdbIdAsync` are used as the fast path when
Trakt/SIMKL have already stored cross-reference IDs.

---

## csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>Chronicle.Plugin.TVMaze</AssemblyName>
    <RootNamespace>Chronicle.Plugin.TVMaze</RootNamespace>
    <Version>1.0.0</Version>
    <Authors>Chronicle Contributors</Authors>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Http" Version="9.0.3" />
    <PackageReference Include="System.Text.Json"          Version="9.0.3" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../Chronicle/src/Chronicle.Plugins/Chronicle.Plugins.csproj"
                      Private="false"
                      ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
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
  "plugin_id":   "chronicle.plugin.tvmaze",
  "name":        "TVMaze",
  "version":     "1.0.0",
  "author":      "Chronicle Contributors",
  "description": "TV series, season, and episode metadata from TVMaze. No API key or account required.",
  "min_chronicle_version": "0.6.0",
  "entry_type":  "Chronicle.Plugin.TVMaze.TvMazeMetadataProvider",
  "iconUrl":     "data:image/png;base64,...",
  "brandColorLight": "#CF0000",
  "brandColorDark":  "#FF2929",
  "fixMatchHint": "Enter a TVMaze show ID (169), a TVMaze URL (https://www.tvmaze.com/shows/169/breaking-bad), a TVDB ID prefixed with thetvdb: (thetvdb:76290), or an IMDB ID (tt0903747).",
  "background_tasks": [
    {
      "task_id":         "fetch-missing-metadata",
      "display_name":    "Fetch Missing TV Metadata",
      "description":     "Fetches TVMaze metadata for TV items that have a TVDB or IMDB ID stored but have not yet been enriched by TVMaze.",
      "default_cron":    "0 4 * * *",
      "default_enabled": true
    },
    {
      "task_id":         "resync-all-metadata",
      "display_name":    "Re-sync All TV Metadata",
      "description":     "Re-fetches all TVMaze metadata to pick up community edits, new episodes, and artwork changes.",
      "default_cron":    "0 2 * * 0",
      "default_enabled": false
    }
  ],
  "settings": []
}
```

`settings` is an empty array — intentional. No configuration UI will appear.

---

## RunTestEnvironment.ps1 Entry

```powershell
@{
    Project    = Join-Path (Split-Path $RepoRoot -Parent) "Chronicle.Plugin.TVMaze\Chronicle.Plugin.TVMaze.csproj"
    DllName    = "Chronicle.Plugin.TVMaze.dll"
    OutputDir  = Join-Path $PluginsDir "chronicle.plugin.tvmaze"
},
```

---

## Key Differences from TheTVDB Plugin

| Concern | TheTVDB | TVMaze |
|---------|---------|--------|
| Auth | JWT token (30-day, refresh required) | None |
| Settings | API key required | None |
| Rate limit handling | Exponential back-off on 429 | Simple 100ms delay + 429 retry |
| HTML in summaries | No | Yes — must strip `<p>` tags |
| Episode cast | Per-episode via extended endpoint | Show-level only (embed on show fetch) |
| Artwork types | Typed array with scores | Typed array with `main` flag |
| Pagination | Episodes paginated (100/page) | All episodes in one call per season |
| Season image | `seasonposter` type artwork | `image` field on season object |
| TVDB cross-ref | Native (it IS the TVDB) | Via `externals.thetvdb` + lookup endpoint |

---

## Things That Can Go Wrong (Preemptive)

| Problem | Prevention |
|---------|-----------|
| Static field init order | Declare `_supportedTypes` array AFTER individual `MediaTypeSupport` fields if split |
| HTML in overview stored raw | Always pass summary through `StripHtml()` before mapping |
| Cast list too long | Cap at 20 members — TVMaze can return 50+ for long-running shows |
| `externals.thetvdb` is null | Guard with `?.` — not all shows have a TVDB cross-ref |
| Lookup returns 404 (unmapped show) | Return empty — fall through to text search |
| `_embedded` null when embed params dropped | Null-check `show.Embedded` — fallback to separate cast/images calls |
| Season IDs differ from season numbers | Always use season `Id` for episode lookup, not `Number` |
| Level 2 cast empty | Expected — document it; episodes don't include cast in TVMaze |
| `ILogger` ambiguous | `using ILogger = Microsoft.Extensions.Logging.ILogger;` at top |

---

## Implementation Order

1. `TvMazeModels.cs` — all records (no logic, easy to verify)
2. `TvMazeClient.cs` — HTTP + rate limiting; test with real API (no key needed)
3. `TvMazeMetadataProvider.cs` — show level first, then season, then episode
4. `manifest.json` + `Chronicle.Plugin.TVMaze.csproj` + icon data URI
5. Build, deploy, test with RunTestEnvironment
6. Add to `RunTestEnvironment.ps1`
7. GitHub repo, initial commit, v1.0.0 release
