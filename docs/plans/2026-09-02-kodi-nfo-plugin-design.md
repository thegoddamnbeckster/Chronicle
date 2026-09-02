# Chronicle.Plugin.Kodi.NFO — Design

**Date:** 2026-09-02
**Status:** Proposed

---

## Why this exists

Two things landed in the same PR (#10) this session, in the wrong place:

1. `Chronicle.Services.Scan.NfoSignalExtractor` / `NfoDetailParser` — parses local `.nfo`
   sidecars during a scan, for matching (title/season/episode/uniqueid) and for the media
   detail page's "NFO details" card (plot, genres, rating, mpaa, premiered/aired, studio,
   director, writers, cast, collection).
2. The lossless-ingestion follow-up (`XmlToJsonConverter`, `FileScannerMetaJson.NfoRaw`/
   `NfoParsed`) — captures the full sidecar, not just those curated fields.

Both live in `Chronicle.Core`/`Chronicle.Services`/`Chronicle.API` — Chronicle's core. That's
a plugin-architecture violation: `.nfo` is Kodi's own sidecar convention (schema, field
names, where Kodi looks for one), not a universal one, and Chronicle's core is supposed to
stay medium-agnostic (see CLAUDE.md's Architecture Rule 1 — "All media type support and
metadata scraping goes through plugin interfaces. Nothing hardcoded"). TMDB, Hardcover,
MusicBrainz all live in their own plugin repos for exactly this reason; NFO parsing should
too.

Separately: `Chronicle_Scraper`'s two Kodi addons (`script.chronicle.scraper.movie` /
`script.chronicle.scraper.tv`) **write** Kodi NFOs today, in Python, from data they fetch off
Chronicle's own `ScraperController` (`GET /movies/details`, `/tv/details`,
`/tv/episode-details`). `lib/nfo_writer.py` / `lib/tv_nfo_writer.py` / `lib/nfo_common.py`
are ~400 lines of field-mapping (Chronicle JSON → Kodi XML) duplicated across two Python
addons, doing the exact inverse of what `NfoDetailParser` does in C#. Per-user direction:
fold the writing side into the same new plugin too, so that duplication can come out of both
scraper repos and live in exactly one place — which also guarantees round-trip fidelity by
construction (whatever the plugin writes, it can read back, because it's the same code).

---

## What can move, and what can't

Read `nfo_writer.py`/`tv_nfo_writer.py`/`nfo_common.py` end to end to separate these
correctly — this is the one finding that shapes the whole design:

| Data | Source | Can move server-side? |
|---|---|---|
| title, plot, tagline, mpaa, premiered/aired, country, studio, status, runtime, genres, tags | Chronicle's own resolved item data | **Yes** |
| cast, director, writers (crew) | Chronicle's own resolved item data | **Yes** |
| `<uniqueid>` (imdb/tmdb/tvdb/trakt) | Chronicle's own `externalIds` | **Yes** |
| `<ratings>` | Chronicle's own resolved ratings | **Yes** |
| `<set>` (collection name/overview) | Chronicle's own collection data | **Yes** |
| `<art>` remote candidates (poster/fanart/clearlogo/banner/clearart/discart/characterart) | Chronicle's own artwork resolution | **Yes** |
| `<trailer>` | Chronicle's own data | **Yes** |
| **`<fileinfo><streamdetails>`** (video/audio/subtitle codec, resolution, HDR, channels, languages) | **`VideoLibrary.GetMovies`/`GetEpisodes` — Kodi's own probe of the real file** | **No.** Confirmed in `movie_art_sync.get_streamdetails()`'s own docstring: *"Chronicle's server has no way to know a file's own codec, resolution, HDR type, or audio/subtitle tracks — only the player that actually opened the file does."* This is a hard boundary, not a convenience choice — Chronicle's server has no video-probing capability and re-implementing one (e.g. via ffprobe) to duplicate what Kodi's C++ core already does correctly would be solving a problem that doesn't need solving. |
| Local art files already sitting on disk (`<video>-poster.jpg`, plain `poster.jpg` for a show root, etc.) | Kodi-local directory listing, Kodi's own naming convention | **Deferred — see "Not solved here" below.** |

So the plugin owns the write side for everything **except** streamdetails and local-art
fallback discovery. Those two stay client-side in the scraper addons, spliced into the
XML the plugin hands back before it's written to disk.

---

## New plugin interface

None of the four existing interfaces (`IMetadataProvider`, `IImportProvider`, `IWidgetPlugin`,
`IThemePlugin`) fit: this isn't a remote search/lookup provider (`IMetadataProvider`'s
`SearchAsync`/`GetByIdAsync` contract), and `IFileScannerPlugin` is a full directory scanner,
not a "given a file, does a sidecar belong to it" hook. New interface, `Chronicle.Plugins`:

```csharp
namespace Chronicle.Plugins;

/// <summary>
/// A plugin that owns one sidecar-metadata format end to end -- both reading it (during a
/// scan, for matching signal and for lossless capture) and writing it (building a document
/// from Chronicle's own resolved data, for an external tool -- e.g. a Kodi scraper addon --
/// to write to disk). Kept as one interface, not two, because round-trip fidelity is the
/// whole point: whatever BuildAsync produces must be exactly what ExtractSignal/
/// CaptureLossless can read back. Chronicle_Scraper's Chronicle.Plugin.Kodi.NFO is the first
/// (and, for now, only) implementation, for Kodi's .nfo convention specifically.
/// </summary>
public interface ISidecarFormatPlugin
{
    // ── Identity ──────────────────────────────────────────────────────────────
    string PluginId { get; }
    string Name     { get; }
    string Version  { get; }
    string Author   { get; }

    MediaTypeSupport[] GetSupportedMediaTypes();
    PluginSettingsSchema GetSettingsSchema();
    void Configure(IReadOnlyDictionary<string, string> settings);

    // ── Read side (scan time) ────────────────────────────────────────────────

    /// <summary>Given a media file's path, returns the sidecar file this plugin's
    /// convention says belongs to it, or null. Chronicle's scan pipeline doesn't know what
    /// a sidecar looks like for any given format -- only the plugin does (e.g. Kodi's
    /// "prefer &lt;video-stem&gt;.nfo, exclude tvshow.nfo/season*.nfo" rule).</summary>
    string? FindSidecar(string mediaFilePath);

    /// <summary>Extracts the minimum signal Chronicle's own scan-time matching needs
    /// (title, year, season/episode, a primary external id) from a sidecar found via
    /// FindSidecar. Null if unreadable/malformed -- never throws.</summary>
    SidecarSignal? ExtractSignal(string sidecarPath);

    /// <summary>Full lossless capture for storage: raw text plus a generic structured
    /// parse. Chronicle persists this verbatim into MetadataJson under this plugin's own
    /// source key -- see FileScannerMetaJson's own doc for why raw text (not just the
    /// parse) is the actual lossless-ingestion guarantee.</summary>
    SidecarCapture? CaptureLossless(string sidecarPath);

    // ── Write side (on demand, via API) ──────────────────────────────────────

    /// <summary>Builds a sidecar document for one item from Chronicle's own resolved data.
    /// extraFields carries data ONLY the caller has and Chronicle's server structurally
    /// cannot (e.g. Kodi's own streamdetails probe) -- an opaque bag the plugin knows how
    /// to splice in; Chronicle's server never inspects or validates its contents.
    /// Returns the exact bytes to write to disk (UTF-8, correct declaration/encoding for
    /// the format).</summary>
    Task<byte[]> BuildAsync(SidecarBuildRequest request,
        IReadOnlyDictionary<string, JsonElement>? extraFields = null,
        CancellationToken ct = default);
}

public record SidecarSignal(
    string? Title, int? Year, int? Season, int? Episode,
    string? ShowTitle, string? ExternalId, string? PosterUrl);

public record SidecarCapture(string RawText, JsonElement? Parsed);

/// <summary>"movie" | "tvshow" | "episode" -- which document shape to build.</summary>
public record SidecarBuildRequest(string Kind, ResolvedMediaData Data);
```

`ResolvedMediaData` reuses (or is built from) whatever `ScraperController`'s existing
`movies/details`/`tv/details`/`tv/episode-details` DTOs already assemble — the plugin isn't
re-deriving Chronicle's resolved view, just serializing it differently than JSON.

### Registry integration

Same pattern as every other plugin category:

```csharp
// IPluginRegistry
IReadOnlyList<ISidecarFormatPlugin> GetSidecarFormatPlugins();
ISidecarFormatPlugin? GetSidecarFormatPlugin(string pluginId);

// LoadedPlugin
IReadOnlyList<ISidecarFormatPlugin> SidecarFormatPlugins { get; }
```

---

## Chronicle core changes (the part that currently violates plugin-first)

`Chronicle.Core.Helpers.XmlToJsonConverter` **stays in Core** — it's genuinely
format-agnostic (any XML, no Kodi knowledge), same category as `DigitParsingHelper`.
Everything else Kodi-specific moves out:

- `Chronicle.Services.Scan.NfoSignalExtractor` and `NfoDetailParser` — **delete**, logic
  moves into `Chronicle.Plugin.Kodi.NFO`'s `ExtractSignal`/`CaptureLossless`.
- `ScanGroupingService.Group()` / `BuiltInFileScannerPlugin.ParseFile()` — replace the
  direct `_nfo.FindSidecar(path)` / `_nfo.Extract(...)` calls with a loop over
  `_registry.GetSidecarFormatPlugins()`, taking the first plugin that both supports the
  media type and returns a non-null `FindSidecar` result. No sidecar-format plugin
  installed → no sidecar capture at all, same as no metadata provider installed → no
  enrichment. This is what actually fixes the architecture complaint: Chronicle's scan
  no longer knows what an NFO is, only that "some installed plugin might".
- `FileScanService`'s `LookupNfo`/`TryReadNfoRaw` (added this session for
  `ImportApprovedAsync`/`ImportDirectAsync`/`ImportSingleFileAsync`) — same change, go
  through the registry instead of a hardcoded `NfoSignalExtractor` field.
- `FileScannerMetaJson`/`FileScannerMetaDto`'s `NfoPath`/`NfoRaw`/`NfoParsed` fields — kept,
  but reframed as "whatever the winning sidecar-format plugin captured," not NFO-specific
  by name. (Renaming is a nice-to-have, not required — `metadata_json` fields are internal
  storage, not public API surface that needs to stay generic-looking.)
- New: `ScraperController` (or a small new controller) gains an endpoint per document kind,
  e.g. `GET /api/v1/scraper/movies/{id}/sidecar/{pluginId}` → resolves the item the same way
  `movies/details` already does, calls `ISidecarFormatPlugin.BuildAsync`, returns the bytes
  with the right content-type. This is the endpoint the scraper addons call instead of
  building XML themselves.

---

## Kodi_Scraper changes (both addons)

- `lib/nfo_common.py`'s Chronicle-data functions — **delete**: `add_actors`,
  `add_directors_and_writers`, `add_uniqueids`, `add_ratings`, `build_art_block`'s
  remote-candidate half. All of it is now server-side.
- `lib/nfo_common.py`'s Kodi-local functions — **keep**: `add_streamdetails`,
  `list_local_art_prefixed`/`list_local_art_plain` (until local-art discovery moves
  server-side too, see below), `add_text` (still needed to splice streamdetails/local-art
  into the fetched document).
- `lib/nfo_writer.py`/`lib/tv_nfo_writer.py`'s `_build_*_nfo()` functions — **delete**.
  `sync_movie_nfo()`/`sync_episode_nfo()`/`sync_tvshow_nfo()` become: call the new
  Chronicle endpoint → parse the returned XML back into an `ElementTree` → splice in
  `<fileinfo><streamdetails>` (from `get_streamdetails()`, unchanged) and any local-art
  fallback the plugin's `<art>` block left empty → write to disk. Same file-location
  resolution, same "preserve prior NFO content via legacy_nfo.py before overwrite" step,
  same rebuild-gating (`rebuild_state.is_active()`) — none of that changes.
- Net: each addon's own NFO-building code shrinks from ~400 shared + ~150 per-addon lines
  to roughly the streamdetails-splice + local-art-splice + file-write plumbing. The
  Chronicle-schema knowledge (what a `<uniqueid>` block looks like, which crew job titles
  map to `<credits>`, the art-tag list) exists in exactly one place instead of three
  (movie addon, TV addon, and now Chronicle's own reader).

---

## Not solved here (explicitly deferred)

- **Local-art-file fallback discovery.** Kodi's naming convention
  (`<video-basename>-poster.jpg`, or plain `poster.jpg` for a show's own folder) differs
  from Chronicle's own local-poster convention (`poster.jpg`/`folder.jpg`/`cover.jpg` only,
  no per-slot naming). Chronicle's file scanner already enumerates every file in a scanned
  folder, so teaching it Kodi's art-naming convention too is *possible*, but it's a second,
  separable piece of work with its own naming-convention design questions — not bundled into
  this plugin's first version. Until then, `list_local_art_prefixed`/`list_local_art_plain`
  stay in the scraper addons exactly as they are today.
- **A real plugin manifest/build for `Chronicle.Plugin.Kodi.NFO`** — this doc defines the
  interface and the migration; scaffolding the actual sibling repo (csproj, manifest.json,
  the C# port of `nfo_common.py`'s Chronicle-data functions and `NfoDetailParser`'s field
  mapping) is the implementation pass this design unblocks, not part of the design itself.

---

## Phased rollout (so this isn't one unreviewable cross-repo change)

1. Add `ISidecarFormatPlugin` to `Chronicle.Plugins`, registry plumbing in
   `Chronicle.Services.Plugins` — additive, no behavior change yet (nothing implements it).
2. Scaffold `Chronicle.Plugin.Kodi.NFO`: port `NfoSignalExtractor`/`NfoDetailParser`'s read
   side first (lower risk — same behavior Chronicle already has, just relocated). Wire
   `ScanGroupingService`/`BuiltInFileScannerPlugin`/`FileScanService` to go through the
   registry. Delete the old `Chronicle.Services.Scan` NFO classes once this plugin is the
   sole caller.
3. Add `BuildAsync` (the write side) to the same plugin, plus the new `ScraperController`
   endpoint(s).
4. Update `Chronicle_Scraper` (movie addon) to call the new endpoint instead of
   `nfo_writer.py`'s local builder; verify against a real library before touching TV.
5. Same for `tv_addon`.
6. Delete the now-dead Chronicle-data functions from `lib/nfo_common.py` in both addons.

Each step is independently shippable and revertible; nothing requires steps 4-6 to land in
the same PR as 1-3.
