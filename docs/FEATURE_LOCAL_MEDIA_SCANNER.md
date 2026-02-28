# Feature Design: Local Network Media Scanner

**Status:** Design/Planning
**Target:** Phase 3
**Goal:** Scan local drives and network shares for audio and video files, then pre-populate the Chronicle database with matched media items — giving new users an immediate populated library without manual entry.

---

## Overview

The scanner discovers media files on the local filesystem and mapped network shares (SMB/UNC paths), extracts metadata from file names and embedded tags (ID3, MP4, MKV headers), attempts to match each file against the Chronicle database (and optionally against a metadata provider like TMDB or MusicBrainz), and persists the results as `MediaItem` + `UserLibrary` records.

---

## Architecture

### New components

| Component | Location | Responsibility |
|---|---|---|
| `IMediaScanner` | `Chronicle.Services/Scanner/` | Service interface |
| `MediaScannerService` | `Chronicle.Services/Scanner/` | Orchestration logic |
| `FileDiscoverer` | `Chronicle.Services/Scanner/` | Recursively find media files |
| `MetadataExtractor` | `Chronicle.Services/Scanner/` | Extract tags from file headers |
| `MediaMatcher` | `Chronicle.Services/Scanner/` | Match discovered files to DB items |
| `ScanSession` (EF model) | `Chronicle.Core/Models/` | Persisted scan job with progress |
| `ScannerController` | `Chronicle.API/Controllers/` | REST API for scanner management |
| `IScannerPlugin` (interface) | `Chronicle.Plugins/` | Optional plugin hook for custom extraction |

---

## File Discovery

### Supported formats

| Category | Extensions |
|---|---|
| Video | `.mkv .mp4 .avi .m4v .mov .wmv .webm .flv .ts .m2ts` |
| Audio | `.mp3 .flac .ogg .opus .m4a .aac .wma .wav .alac .ape` |
| Subtitles | `.srt .ass .ssa` (linked to nearest video file) |

### Scan paths

Configured in `appsettings.json` under `"Scanner:Paths"`:

```json
{
  "Scanner": {
    "Paths": [
      "D:\\Media\\Movies",
      "D:\\Media\\TV",
      "\\\\NAS\\Music",
      "/mnt/nas/media"
    ],
    "ExcludePatterns": ["**/sample/**", "**/*.sample.*", "**/extras/**"],
    "FollowSymlinks": false,
    "MaxDepth": 10
  }
}
```

Paths are stored per-user in the `app_settings` table so multi-user setups can each have their own library roots.

### Discovery algorithm

```
For each root path:
  Walk directory tree (breadth-first, bounded by MaxDepth)
  For each file:
    If extension matches supported list → add to discovered queue
    Skip if path matches any ExcludePattern glob
    Skip if file was already seen in a previous scan (by absolute path + mtime)
```

---

## Metadata Extraction

### Video files

1. **File name parsing** — Uses regex patterns to extract title, year, quality tag:
   - `Movie.Title.2023.1080p.BluRay.mkv` → title="Movie Title", year=2023
   - `Show.Name.S02E05.Episode.Title.mp4` → show="Show Name", season=2, episode=5
   - `Show Name - 2x05 - Episode Title.mkv` → same
2. **Embedded metadata** — Uses `TagLib#` (NuGet: `TagLibSharp`) to read MP4/MKV metadata tags
3. **NFO sidecar** — Reads Kodi-compatible `.nfo` XML files if present alongside the video

### Audio files

1. **Embedded ID3/Vorbis/FLAC tags** — Artist, Album, Track, Year, Genre via `TagLibSharp`
2. **Folder structure** — `Artist/Album/01 - Track.flac` as fallback

### Extraction priority: embedded tags > NFO sidecar > filename parsing

---

## Media Matching

### Match pipeline (in order)

1. **External ID match** — If NFO contains TMDB/IMDB ID, look up `media_external_ids` directly
2. **Database title+year match** — `media_items WHERE name = ? AND year = ?`
3. **Fuzzy title match** — Levenshtein distance ≤ 2 on normalized title (lowercase, strip articles)
4. **Metadata provider lookup** — If no local match and a provider plugin is loaded, call `IMetadataProvider.SearchAsync` to find and enrich the item
5. **Stub creation** — If nothing matched, create a stub `MediaItem` with extracted data

### Match confidence levels

| Level | Condition | Action |
|---|---|---|
| `Exact` | External ID match OR title+year exact | Auto-confirm |
| `High` | Fuzzy match ≤ 1 OR metadata provider match | Auto-confirm (configurable) |
| `Low` | Fuzzy match 2 OR title-only match | Queue for user review |
| `None` | No match found | Create stub, flag for review |

---

## Scan Sessions (Progress Tracking)

A `ScanSession` model tracks each scan job:

```csharp
public class ScanSession
{
    public int    Id          { get; set; }
    public int    UserId      { get; set; }
    public string Status      { get; set; }  // queued | running | completed | failed
    public int    TotalFiles  { get; set; }
    public int    Processed   { get; set; }
    public int    Matched     { get; set; }
    public int    Created     { get; set; }
    public int    Skipped     { get; set; }
    public DateTime StartedAt  { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string  PathsJson   { get; set; }  // JSON array of scanned roots
}
```

Scans run as a `BackgroundService` (or `IHostedService`). The REST API streams progress via Server-Sent Events (SSE) on `GET /api/v1/scanner/{sessionId}/progress`.

---

## REST API

```
POST   /api/v1/scanner/scan          Start a new scan (body: { paths[], options })
GET    /api/v1/scanner/sessions      List all scan sessions for current user
GET    /api/v1/scanner/{id}          Get session status + stats
GET    /api/v1/scanner/{id}/progress SSE stream of progress events
DELETE /api/v1/scanner/{id}          Cancel a running scan
GET    /api/v1/scanner/{id}/unmatched  List files that need user review
POST   /api/v1/scanner/{id}/confirm   Confirm a match or mapping decision
GET    /api/v1/scanner/paths         Get configured scan paths for current user
PUT    /api/v1/scanner/paths         Update scan paths
```

---

## IScannerPlugin — Extension Point

Plugins can hook into the scanner to add custom extraction logic (e.g. reading proprietary sidecar formats, parsing anime-specific naming conventions):

```csharp
public interface IScannerPlugin
{
    string PluginId  { get; }
    string Name      { get; }

    /// <summary>Called for each discovered file before standard extraction.</summary>
    Task<ExtractedFileMetadata?> TryExtractAsync(string filePath, CancellationToken ct);
}

public record ExtractedFileMetadata(
    string?  Title,
    int?     Year,
    string?  MediaType,
    string?  Artist,
    string?  Album,
    int?     Season,
    int?     Episode,
    IReadOnlyDictionary<string, string> ExternalIds
);
```

---

## NuGet Dependencies

| Package | Use |
|---|---|
| `TagLibSharp` | Read audio/video embedded tags |
| `Microsoft.Extensions.FileSystemGlobbing` | Exclude pattern matching |

---

## Implementation Order

1. Add `ScanSession` model + EF migration
2. Implement `FileDiscoverer` + `MetadataExtractor` (TagLibSharp integration)
3. Implement `MediaMatcher` (DB lookup + fuzzy match)
4. Implement `MediaScannerService` background job + progress events
5. Add `ScannerController` REST endpoints
6. Add `IScannerPlugin` interface to `Chronicle.Plugins`
7. Frontend: Scanner settings page, progress UI, unmatched items review queue

---

## Configuration Example

```json
{
  "Scanner": {
    "Paths": ["D:\\Media"],
    "ExcludePatterns": ["**/sample/**", "**/@eaDir/**"],
    "AutoConfirmHighConfidence": true,
    "UseMetadataProviderForLowConfidence": true,
    "LibraryStatusForMatched": "Completed",
    "LibraryStatusForUnwatched": "PlanToWatch"
  }
}
```
