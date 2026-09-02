# Chronicle.Plugin.Kodi.NFO — Design

**Date:** 2026-09-02
**Status:** Read side + core rewiring done (`NfoSignalExtractor`/`NfoDetailParser` deleted).
Write side done on the Chronicle side: `ScraperController` now has sidecar-building
endpoints (phased-rollout step 3). Steps 4-6 -- updating the two `Chronicle_Scraper` Kodi
addons to actually call them -- are not started; that work lives in a separate repo not
available to this session.

---

## Progress checkpoint (2026-09-02, updated)

**Update:** the one remaining blocker noted below — `MetadataEnrichmentService.TryReadNfoTmdbId`'s
direct `NfoSignalExtractor` dependency — is now fixed. Rather than threading `IPluginRegistry`
down through the `EnrichItemCoreAsync` → `EnrichItemCoreLockedAsync` call chain as originally
planned, the actual fix turned out smaller: `MetadataEnrichmentService` already captures
`IServiceScopeFactory scopeFactory` as a primary-constructor parameter and already uses the
`scopeFactory.CreateScope()`/`CreateAsyncScope()` → resolve-from-scope idiom throughout this
file (it's a scoped service pulled in on demand because `IPluginRegistry` is itself scoped and
this class needs fresh instances across long-running background batches). `TryReadNfoTmdbId`
now just does the same thing locally — `using var scope = scopeFactory.CreateScope();` then
`scope.ServiceProvider.GetRequiredService<IPluginRegistry>().GetSidecarFormatPlugins()` — with
no change needed anywhere in the call chain above it. It probes each well-known filename
(`tvshow.nfo`, `movie.nfo`) through each plugin's own `FindSidecar` convention (recommendation
1 from the original write-up below: `plugin.FindSidecar(Path.Combine(folderPath, "tvshow.nfo"))`
resolves to itself via Kodi's own stem-match rule) rather than reimplementing that convention
in this file.

With that closed out, `Chronicle.Services.Scan.NfoSignalExtractor`/`NfoDetailParser` had no
remaining callers anywhere in `src/` and were deleted, along with their direct tests
(`NfoSignalExtractorTests`, `NfoDetailParserTests.cs` — coverage already existed in the
`Chronicle.Plugin.Kodi.NFO` repo's own `KodiNfoReaderTests.cs`). Phased-rollout step 2 (below)
is now fully done.

### Original checkpoint (superseded by the update above, kept for the full trail)

What's actually done, as of this checkpoint, so a later session doesn't have to re-derive it
by diffing:

**`Chronicle.Plugin.Kodi.NFO`** (separate repo, `thegoddamnbeckster/Chronicle.Plugin.Kodi.NFO`,
branch `main`) — scaffolded and implements the read side in full: `KodiNfoReader`
(`FindSidecar`, `ExtractSignal`, `CaptureLossless`, `ExtractCuratedFields`) and `KodiNfoBuilder`
(`BuildAsync` for movie/show/episode) both exist and are unit-tested. This is further along
than the interface sketch below shows — see that repo directly for the real, current
`ISidecarFormatPlugin` implementation rather than trusting the code blocks in this doc, which
were written before implementation and have drifted in small ways (e.g. `SidecarSignal` also
carries `Artist`/`Album` for music-type grouping, `BuildAsync` takes a single
`SidecarBuildRequest` whose `ExtraFields` init property carries the streamdetails bag rather
than a separate parameter).

**Main `Chronicle` repo, PR #10** — the plugin/registry plumbing (`ISidecarFormatPlugin`,
`SidecarModels.cs`, `PluginRegistry`/`IPluginRegistry`/`LoadedPlugin` wiring — step 1 below)
and the rewiring of everything that's real, DI-registered and can reach `IPluginRegistry` —
step 2 below — are both done:

- `ScanGroupingService` — constructor takes `IPluginRegistry`; per-file sidecar lookup goes
  through `GetSidecarFormatPlugins()`.
- `FileScanService` — `LookupNfo`, a new `CaptureSidecar` helper, and `UpsertGroupItemAsync`
  all resolve sidecars through the registry instead of the old hardcoded classes. A new
  `ApplyNfoSignals` method is a **compensating post-scan pass**: `BuiltInFileScannerPlugin`
  is instantiated via bare `Activator.CreateInstance` (see
  `PluginRegistry.DiscoverAndInstantiate`) with **no DI**, so it can never itself hold an
  `IPluginRegistry` reference to look up sidecar plugins. `ApplyNfoSignals` runs once against
  every `ScanDirectoryAsync` result (4 call sites) and reproduces exactly the field-priority
  rules `BuiltInFileScannerPlugin.ParseFile` used to apply directly against the old
  `NfoSignalExtractor`.
- `BuiltInFileScannerPlugin` — no longer touches any sidecar class; produces tag/filename-only
  fields and leaves sidecar fields null for `ApplyNfoSignals` to fill in.
- `MediaController.GetNfoDetail` — delegates to whichever loaded `ISidecarFormatPlugin`'s
  `ExtractCuratedFields` recognizes the path, returns `JsonElement` (same JSON shape the
  frontend's `NfoDetail` TS interface already expects) instead of the removed dependency on a
  typed `Chronicle.Services.Scan.NfoDetail`.
- `Program.cs` — the `NfoSignalExtractor`/`NfoDetailParser` DI registrations are removed
  (nothing above needs them anymore).
- Tests updated to match: `ScanGroupingServiceTests` gained a `FakeNfoSidecarPlugin` test
  double (file-scoped, in that test file) since the real Kodi logic lives in the separate
  plugin repo this test project can't reference; `FileScanServiceHierarchyTests`' four direct
  `FileScanService(...)` construction sites lost the arg the retired constructor parameter
  needed.

**What's NOT done — the one remaining blocker before `NfoSignalExtractor.cs`/
`NfoDetailParser.cs` can be deleted:**

`Chronicle.Services.MetadataEnrichmentService` has its own, independent, previously
undiscovered direct dependency:

```csharp
private static readonly NfoSignalExtractor _nfoExtractor = new();
```

used by `TryReadNfoTmdbId(string folderPath)` (private static, ~line 2343), which hardcodes
Kodi's own well-known filenames (`tvshow.nfo`, `movie.nfo`, falling back to the first `*.nfo`
in the folder) and calls `_nfoExtractor.Extract(path)?.ExternalId` to pull a TMDB id straight
out of a local sidecar as a fallback match strategy before falling back to a name search (see
the surrounding comment at ~line 1343, "NFO sidecar fallback (root items only, TMDB-style
plugins)"). This is exactly the same kind of hardcoded-Kodi-knowledge-in-core problem the rest
of this doc is about — it was simply never surfaced until this rewiring pass actually grepped
for every real caller of `NfoSignalExtractor`.

Unlike `BuiltInFileScannerPlugin`, `MetadataEnrichmentService` **is** a real DI-registered
service (`AddScoped<IMetadataEnrichmentService, MetadataEnrichmentService>` in `Program.cs`)
and already resolves `IPluginRegistry` in several places via
`scope.ServiceProvider.GetRequiredService<IPluginRegistry>()` (a scoped-background-job
pattern, not constructor injection, because this service runs enrichment batches outside a
normal request scope). So this is fixable the same way as everything above — it just wasn't
done in this pass because it surfaced late and threading a registry reference down to
`TryReadNfoTmdbId` touches a call chain, not a single method:

- `TryReadNfoTmdbId(folderPath)` is called from `EnrichItemCoreLockedAsync` (~line 1356),
  which has no `IPluginRegistry` in its parameter list today (`db, provider, pluginId, row,
  options, ct, allProviders`).
- `EnrichItemCoreLockedAsync` is called from `EnrichItemCoreAsync` (~line 1015) — **note
  there are two distinct `EnrichItemCoreAsync` overloads/definitions in this file** (~line
  992 and ~line 2489); the second appears to be part of a separate cascade-to-children path
  (`CascadeToChildrenAsync`, ~line 2535) and needs the same treatment independently — check
  whether it also reaches `EnrichItemCoreLockedAsync` or has its own parallel logic before
  assuming one fix covers both.
- `EnrichItemCoreAsync` in turn has ~4-5 callers across the file (grep `EnrichItemCoreAsync(`
  for the current list — lines drift as the file changes), each of which already resolves
  `registry` locally via the `scope.ServiceProvider.GetRequiredService<IPluginRegistry>()`
  pattern for its own purposes.

**The fix, mechanically:** thread an `IPluginRegistry registry` parameter (or just the
resolved `IReadOnlyList<ISidecarFormatPlugin>`, to keep the signature smaller) down through
`EnrichItemCoreAsync` → `EnrichItemCoreLockedAsync` → `TryReadNfoTmdbId`, at every call site
listed above, then rewrite `TryReadNfoTmdbId` to loop over the sidecar plugins the same way
`ScanGroupingService.FindSidecarSignal`/`FileScanService.LookupNfo` do — except this one looks
in a folder for a set of **well-known filenames** rather than next to a specific media file,
which `ISidecarFormatPlugin.FindSidecar(mediaFilePath)` doesn't directly support. Two
reasonable options, pick one when doing this:
1. Call `plugin.FindSidecar(Path.Combine(folderPath, "tvshow.nfo"))` (or `"movie.nfo"`) as a
   synthetic "media file path" — works today because Kodi's own `FindSidecar` just does
   stem-based lookup (`tvshow.nfo` → looks for `tvshow.nfo`, finds itself) but is a slightly
   abusive use of the contract and only happens to work for this one plugin's
   implementation.
2. Add a small folder-scoped lookup to the interface (e.g. `string?
   FindWellKnownSidecar(string folderPath, string[] candidateNames)`) — cleaner, but touches
   the interface again, meaning the plugin repo needs a follow-up release too.
Recommendation: option 1 to start (no interface/plugin-repo change needed, ships in this repo
alone), revisit option 2 only if a second sidecar-format plugin ever needs the same
well-known-filename pattern and the abuse becomes uncomfortable.

Once that's done: delete `TryReadNfoTmdbId`'s dependency on `NfoSignalExtractor`, then check
system-wide (`grep -rn "NfoSignalExtractor\|NfoDetailParser"` across `src/` and `tests/`) —
at that point only the two class definitions themselves and `NfoSignalExtractorTests`/
`NfoDetailParserTests` (which test them directly, and can be deleted alongside) should remain,
and steps 2's final "delete the old classes" sub-step (see Phased rollout below) can actually
happen.

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

1. ✅ **Done.** Add `ISidecarFormatPlugin` to `Chronicle.Plugins`, registry plumbing in
   `Chronicle.Services.Plugins` — additive, no behavior change yet (nothing implements it).
2. ✅ **Done.**
   - ✅ Scaffold `Chronicle.Plugin.Kodi.NFO`: port `NfoSignalExtractor`/`NfoDetailParser`'s
     read side (`KodiNfoReader`).
   - ✅ Wire `ScanGroupingService`/`FileScanService` to go through the registry;
     `BuiltInFileScannerPlugin` stripped of direct sidecar knowledge with a compensating
     `ApplyNfoSignals` post-scan pass in `FileScanService` (it can't reach the registry
     itself — see Progress checkpoint for why).
   - ✅ Wire `MediaController.GetNfoDetail` through the registry.
   - ✅ `MetadataEnrichmentService.TryReadNfoTmdbId` rewired to resolve sidecar plugins via
     `scopeFactory.CreateScope()` (see the update at the top of the Progress checkpoint).
   - ✅ `Chronicle.Services.Scan.NfoSignalExtractor`/`NfoDetailParser` and their direct tests
     (`NfoSignalExtractorTests`, `NfoDetailParserTests.cs`) deleted — no remaining callers.
3. ✅ **Done.** `KodiNfoBuilder.BuildAsync` already existed in the plugin repo, unit-tested in
   isolation; `ScraperController` now has the endpoints that resolve an item's data and
   invoke it:
   - `GET /api/v1/scraper/movies/sidecar?id={id}&pluginId={pluginId}`
   - `GET /api/v1/scraper/tv/sidecar?id={id}&pluginId={pluginId}`
   - `GET /api/v1/scraper/tv/episode-sidecar?id={id}&pluginId={pluginId}`
   (query-param style, `pluginId` optional — matches this controller's existing
   `movies/details`/`tv/details`/`tv/episode-details` convention rather than the path-segment
   sketch in the original write-up above; `pluginId` defaults to the first loaded
   `ISidecarFormatPlugin` when omitted.) Each endpoint reuses the exact same resolved-data
   assembly the JSON `*/details` endpoints already had (`GetMovieDetails`/`GetShowDetails`/
   `GetEpisodeDetails` were refactored to share `BuildMovieDetailsDtoAsync`/
   `BuildShowDetailsDtoAsync`/`BuildEpisodeDetailsDtoAsync` with the new sidecar endpoints,
   rather than duplicating collection/season/artwork resolution a second time), then maps the
   `Chronicle.API.DTOs` shapes to `Chronicle.Plugins.Models`' `Resolved*Data` shapes (the two
   are deliberately separate types in separate assemblies -- see `ScraperController`'s own
   "Scraper DTO -> plugin resolved-data mapping" section) and calls
   `ISidecarFormatPlugin.BuildAsync`, returning the raw bytes as
   `application/octet-stream` (deliberately not `application/xml` -- the controller has no
   business assuming every future sidecar plugin is XML-shaped). No `ExtraFields`/
   streamdetails handling here: per the design above, the addon splices those in itself
   client-side after fetching the built XML, so this endpoint never needs them.
4. Update `Chronicle_Scraper` (movie addon) to call the new endpoint instead of
   `nfo_writer.py`'s local builder; verify against a real library before touching TV.
   **Not started** — lives in a separate repo not available to this session.
5. Same for `tv_addon`. **Not started** (same reason).
6. Delete the now-dead Chronicle-data functions from `lib/nfo_common.py` in both addons.
   **Not started** (same reason).

Each step is independently shippable and revertible; nothing requires steps 4-6 to land in
the same PR as 1-3.
