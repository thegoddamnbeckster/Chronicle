# MusicBrainz Plugin + Generic Metadata Enrichment Infrastructure
**Date:** 2026-03-20
**Status:** Approved

---

## Overview

Two parallel workstreams:

1. **Generic enrichment infrastructure** — core schema + service that any metadata plugin (TMDB, MusicBrainz, Last.fm, Trakt, Simkl, etc.) uses for background per-item enrichment
2. **MusicBrainz plugin** — standalone project at `W:\Scripts\Chronicle.Plugin.MusicBrainz`, fetching every available field from the MusicBrainz API and Cover Art Archive

Also in scope: **move Chronicle.Plugins.TMDB** out of the main repo into its own standalone project at `W:\Scripts\Chronicle.Plugin.TMDB`, matching the FileScanner pattern.

---

## Part 1 — Core Schema: `media_item_enrichment_status`

### Table Definition

```sql
CREATE TABLE media_item_enrichment_status (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    media_item_id      INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
    plugin_id          TEXT    NOT NULL,   -- e.g. "chronicle.plugin.musicbrainz"
    external_id        TEXT,              -- resolved ID in this plugin's namespace
    status             TEXT    NOT NULL DEFAULT 'pending',
    retry_count        INTEGER NOT NULL DEFAULT 0,
    max_retries        INTEGER NOT NULL DEFAULT 3,
    last_attempted_at  DATETIME,
    last_completed_at  DATETIME,
    error_message      TEXT,
    UNIQUE(media_item_id, plugin_id)
);
```

### Status Values

| Status | Meaning |
|--------|---------|
| `pending` | Not yet attempted, or manually reset |
| `completed` | Successfully enriched |
| `failed` | Last attempt failed, retries remain |
| `exhausted` | `retry_count >= max_retries` — skipped by enrichment service |
| `not_found` | Plugin returned no match — skipped permanently |
| `skipped` | User manually marked "do not enrich" |

### Lifecycle Rules

- **On plugin install:** All existing items of the plugin's supported media types get a `pending` row inserted
- **On failure:** `retry_count++`; if `retry_count >= max_retries` → status becomes `exhausted`
- **Retry window:** Failed (non-exhausted) items are re-attempted after 24h (configurable)
- **Enrichment service skips:** `exhausted`, `not_found`, `skipped`
- **`max_retries`** is stored per-row (set from plugin setting at insert time) so changing the plugin setting affects future rows without breaking existing ones

### Reset Operations (per item+plugin)

| Operation | Effect |
|-----------|--------|
| Reset single item | `status='pending'`, `retry_count=0`, `error_message=NULL` |
| Reset all exhausted for plugin | Bulk reset all `exhausted` rows for a given `plugin_id` |
| Reset all for plugin | Bulk reset all non-`skipped` rows for a given `plugin_id` |
| Skip item | `status='skipped'` — never retried unless explicitly reset |

### EF Core Model

```csharp
// Chronicle.Core/Models/MediaItemEnrichmentStatus.cs
public class MediaItemEnrichmentStatus
{
    public int Id { get; set; }
    public int MediaItemId { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public string? ExternalId { get; set; }
    public EnrichmentStatus Status { get; set; } = EnrichmentStatus.Pending;
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public DateTime? LastAttemptedAt { get; set; }
    public DateTime? LastCompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public MediaItem? MediaItem { get; set; }
}

public enum EnrichmentStatus
{
    Pending, Completed, Failed, Exhausted, NotFound, Skipped
}
```

---

## Part 2 — Generic Metadata Enrichment Service

### `IMetadataEnrichmentService` (Chronicle.Services)

```csharp
public interface IMetadataEnrichmentService
{
    Task EnrichPendingAsync(string pluginId, CancellationToken ct = default);
    Task EnrichAllAsync(CancellationToken ct = default);  // all plugins
    Task ResetAsync(string pluginId, ResetScope scope, int? mediaItemId = null);
    Task SkipAsync(int mediaItemId, string pluginId);
}

public enum ResetScope { Single, AllExhausted, AllForPlugin }
```

### `MetadataEnrichmentService` Behaviour

1. Query DB for items with `status IN (pending, failed)` where `last_attempted_at` is null or > retry window
2. For each item, resolve the matching `IMetadataProvider` from the plugin registry
3. If `external_id` already known (e.g. from file tags/NFO): call `GetByIdAsync`
   Otherwise: call `SearchAsync` using item title + artist/year hints
4. On success: merge returned data into `media_items.metadata_json[plugin_id]`, update images, set `status='completed'`
5. On failure: increment `retry_count`, set `status='failed'` or `'exhausted'`, store error
6. Rate limiting is **the plugin's responsibility** — the service calls sequentially and trusts the plugin

### `MetadataEnrichmentScheduledTask` (IScheduledTask)

```
TaskId:      "metadata_enrichment"
TaskName:    "Metadata Enrichment"
DefaultCron: "0 4 * * *"   (4am nightly, after the 3am file scan)
```

Also fires automatically (async, non-blocking) after `ScheduledScanService` completes a scan — a new import batch triggers enrichment for those items only.

---

## Part 3 — MusicBrainz Plugin

### Project Location

`W:\Scripts\Chronicle.Plugin.MusicBrainz\`
Mirrors FileScanner structure — standalone project referencing `Chronicle.Plugins.dll` with `<Private>false</Private>`.

### Plugin Identity

```
plugin_id:   chronicle.plugin.musicbrainz
name:        MusicBrainz
version:     1.0.0
author:      Chronicle Contributors
entry_type:  Chronicle.Plugin.MusicBrainz.MusicBrainzMetadataProvider
```

### Supported Media Types

| Media Type | Level | Notes |
|------------|-------|-------|
| `music`    | Artist (root) | Maps to MusicBrainz `artist` entity |
| `music`    | Album (mid)   | Maps to MusicBrainz `release-group` + `release` |
| `music`    | Track (leaf)  | Maps to MusicBrainz `recording` |

### Settings Schema

| Key | Type | Required | Default | Notes |
|-----|------|----------|---------|-------|
| `Username` | Text | No | — | MusicBrainz account username |
| `Password` | Password | No | — | MusicBrainz account password |
| `UserAgent` | Text | Yes | `Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)` | Required by MB API ToS |
| `MaxRetries` | Number | No | `3` | Per-item failure limit |
| `RateLimitMs` | Number | No | `1000` | ms between requests (anonymous); authenticated auto-reduces to 200ms |

### Rate Limiter (internal to plugin)

- `SemaphoreSlim` + timestamp tracking
- Anonymous: 1 req/sec (1000ms between requests)
- Authenticated: 5 req/sec (200ms between requests)
- Configured automatically based on whether Username+Password are set
- All API methods go through the limiter — `SearchAsync`, `GetByIdAsync`, `GetImageAsync`

### Authentication

MusicBrainz uses HTTP Digest authentication. The plugin uses `HttpClientHandler` with `Credentials` when username+password are configured. Authenticated users also get access to:
- User ratings on artists/releases/recordings
- User tags (personal folksonomy)
- Collection membership

### MusicBrainz API Coverage

#### Artist Entity (`/ws/2/artist/{mbid}`)
`inc=recordings+releases+release-groups+works+aliases+tags+genres+ratings+url-rels+artist-rels`

Fields captured:
- Name, sort-name, disambiguation, type (Person/Group/Orchestra/etc.)
- Life-span (begin, end, ended)
- Area, begin-area, end-area
- Aliases (all alternate names and their types/locales)
- Tags + genres (with vote counts)
- Rating (value + votes)
- URL relationships (official site, Discogs, Wikidata, Wikipedia, YouTube, Bandcamp, SoundCloud, AllMusic, IMDb, social media)
- Artist relationships (members, member-of, collaboration partners)
- Release groups (all albums/singles/EPs/etc.)
- External IDs: `artist:{mbid}`

#### Release Group (`/ws/2/release-group/{mbid}`)
`inc=artists+releases+tags+genres+ratings+url-rels`

Fields captured:
- Title, primary-type (Album/Single/EP/Broadcast/Other)
- Secondary types (Compilation/Soundtrack/Spokenword/Interview/Audiobook/Live/Remix/DJ-mix/Mixtape/Demo)
- First release date
- Disambiguation
- Artist credits (all credited artists with join phrases)
- All releases in the group (each with country, date, format, label)
- Tags, genres, rating
- Cover art (from Cover Art Archive — see below)

#### Release (`/ws/2/release/{mbid}`)
`inc=artists+recordings+release-groups+labels+media+tags+genres+url-rels+artist-credits+isrcs`

Fields captured:
- Title, date, country, status (Official/Promotional/Bootleg/Pseudo-Release)
- Barcode, catalog numbers
- Disambiguation
- Label info (label name, catalog number)
- Media list: each disc/medium with format (CD/Vinyl/Digital/Cassette/etc.) and track count
- Complete tracklist with positions, titles, lengths, artist credits
- ISRCs for each recording
- Cover art (Cover Art Archive)
- Packaging type
- Text representation (language, script)
- Quality rating

#### Recording (`/ws/2/recording/{mbid}`)
`inc=artists+releases+tags+genres+isrcs+url-rels+artist-rels+work-rels`

Fields captured:
- Title, length (ms), disambiguation
- First release date
- ISRCs (all associated)
- Artist credits
- Releases that include this recording (with position/disc info)
- Work relationships (links to the underlying composition)
- Artist relationships (composer, lyricist, producer, engineer, performer roles)
- Tags, genres, rating
- Video flag

#### Work (composition, linked from Recording)
`/ws/2/work/{mbid}?inc=artist-rels+url-rels`

Fields captured:
- Title, type (Song/Aria/Sonata/etc.)
- ISWC (international standard musical work code)
- Language
- Composers, lyricists, arrangers (via artist relationships)
- Related works (e.g., arrangement of)

### Cover Art Archive

`https://coverartarchive.org/release/{mbid}` — returns all images with types and URLs.

Image types captured: `Front`, `Back`, `Booklet`, `Medium`, `Tray`, `Obi`, `Spine`, `Track`, `Liner`, `Sticker`, `Poster`, `Watermark`, `Raw/Unedited`, `Matrix/Runout`, `Top`, `Bottom`, `Other`

For release groups: `https://coverartarchive.org/release-group/{mbid}`

All image URLs stored in `metadata_json["musicbrainz"]["images"]` as an array with type, url, thumbnails (250/500/1200), and `is_approved` flag. Chronicle's image selection logic (future) decides which to use as poster/backdrop.

Artist images are fetched via linked Wikimedia Commons URLs from the artist's URL relationships.

### External ID Format

| Level | Format | Example |
|-------|--------|---------|
| Artist | `artist:{mbid}` | `artist:4a4ee089-93b9-4a56-a4f0-9f234f0cb04f` |
| Release Group | `release-group:{mbid}` | `release-group:db3dc6e8-1f97-...` |
| Release | `release:{mbid}` | `release:0a43e4a8-...` |
| Recording | `recording:{mbid}` | `recording:f8a9d8f2-...` |

File tags (from FileScanner/EmbeddedTagReader) already populate `MUSICBRAINZ_ARTISTID`, `MUSICBRAINZ_ALBUMID`, `MUSICBRAINZ_RELEASEGROUPID`, `MUSICBRAINZ_TRACKID` — these feed directly into the enrichment service's `GetByIdAsync` call, skipping search entirely for properly tagged files.

### `metadata_json` Storage

All MusicBrainz data is stored under the `"musicbrainz"` key:

```json
{
  "musicbrainz": {
    "artist": {
      "mbid": "4a4ee089-...",
      "sort_name": "Radiohead",
      "type": "Group",
      "life_span": { "begin": "1985", "ended": false },
      "area": "Abingdon, Oxfordshire, England",
      "aliases": ["..."],
      "tags": [{"name": "art rock", "count": 42}],
      "genres": ["alternative rock", "art rock"],
      "rating": { "value": 4.8, "votes": 1203 },
      "urls": { "official": "https://radiohead.com", "discogs": "...", "wikidata": "..." },
      "members": ["Thom Yorke", "Jonny Greenwood", "..."],
      "images": [{ "type": "artist", "url": "...", "source": "wikimedia" }]
    }
  }
}
```

### File Structure

```
W:\Scripts\Chronicle.Plugin.MusicBrainz\
├── Chronicle.Plugin.MusicBrainz.csproj
├── manifest.json
├── MusicBrainzMetadataProvider.cs      # IMetadataProvider implementation
├── MusicBrainzClient.cs                # HTTP + rate limiting + auth
├── MusicBrainzSearcher.cs              # SearchAsync logic (artist/album/track)
├── MusicBrainzEntityFetcher.cs         # GetByIdAsync — full detail fetch for all entity types
├── CoverArtArchiveClient.cs            # Cover Art Archive image listing
├── MusicBrainzModels.cs                # Deserialisation models (artist, release, recording, work...)
└── MusicBrainzMetadataMapper.cs        # Maps MB models → MediaMetadata + metadata_json shape
```

---

## Part 4 — TMDB Plugin Migration

Move `src/Chronicle.Plugins.TMDB/` → `W:\Scripts\Chronicle.Plugin.TMDB\`

- Update .csproj to reference Chronicle.Plugins via relative path (same pattern as FileScanner)
- Remove project from `Chronicle.sln`
- Add a build/copy step to `publish-windows.ps1` that builds the external plugin and drops the DLL + manifest into the plugins output directory
- No functional changes — purely a project location change

---

## Part 5 — UI: Background Tasks Page Additions

The existing Background Tasks page gets an **Enrichment Status** section per plugin showing:

| Plugin | Pending | Completed | Failed | Exhausted | Skipped |
|--------|---------|-----------|--------|-----------|---------|
| MusicBrainz | 4,821 | 12,043 | 23 | 7 | 2 |
| TMDB | 0 | 3,201 | 0 | 0 | 0 |

Reset buttons: **Reset Exhausted** / **Reset All** per plugin row.
Individual item skip/reset surfaces on the media item detail page (future).

---

## Implementation Order

1. **EF Core migration** — `media_item_enrichment_status` table + `EnrichmentStatus` enum
2. **Chronicle.Core model** — `MediaItemEnrichmentStatus`
3. **Chronicle.Services** — `IMetadataEnrichmentService`, `MetadataEnrichmentService`, `MetadataEnrichmentScheduledTask`
4. **Chronicle.API** — enrichment status endpoints, reset endpoints
5. **TMDB migration** — move to standalone project, update solution + publish scripts
6. **MusicBrainz plugin** — full implementation at `W:\Scripts\Chronicle.Plugin.MusicBrainz`
7. **Frontend** — Background Tasks enrichment status panel + reset UI

---

## Out of Scope (future)

- Last.fm plugin (separate design)
- Trakt / Simkl plugins (separate design)
- Per-item skip/reset on media detail page
- Conflict resolution UI when two plugins disagree on a field
- User rating sync (requires Last.fm/Trakt user auth flow)
