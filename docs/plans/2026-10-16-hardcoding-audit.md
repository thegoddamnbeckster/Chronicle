# Hardcoding audit (2026-10-16)

**Request:** Chronicle itself may not contain hardcoding. Plugins may (they are for specific types and providers by nature).
Minimise the media-type hardcoding, find and address the non-media-type hardcoding, audit everything, and report redundancy and dead code.

**Method.** Every string literal, constant, switch and list in `src/` (Core, Data, Services, API, Web) was searched for four things:
media type names, provider/plugin names, provider id syntax, and tunable numbers or configuration. Each hit was classified as
*fixed*, *protocol vocabulary that has to be written down somewhere*, *historical repair bound to one provider*, or
*technical limit*. Dead code was found with reference counting across `src/` and `tests/`, then confirmed by compiling
and running the tests (a name that appeared only in its own declaration was removed; anything a test or the compiler still needed was kept).

## The rule applied

Chronicle may name three kinds of thing without it being hardcoding:

1. **Protocol vocabulary** other software dictates: the `movie` / `tv` / `music` provider families, Kodi's `uniqueid` types, the
   scrobbler payload kinds, REST and column names.
2. **Technical limits**: page and batch sizes, timeouts, buffer sizes, password work factor, SQLite busy timeout.
3. **Seed data**: the three starting media types, written down once in `BuiltInMediaTypes`, and the starting values of two enrichment settings (in the migration that introduced them).

Everything else is either read from the database, or declared by the plugin it is about.

## Fixed

### Media types are rows, not names

| Was | Now |
|---|---|
| `"people"` compared in ~40 places | `media_types.IsPersonType`; `MediaTypeRegistry.PersonType` / `IsPersonType(name)` |
| `"movies"` as the default for unknown items, in 9 places | `media_types.IsDefaultType` |
| five copies of "`movies` -> `movie`" provider-name normalisation | one: `MediaTypeRegistry.ProviderName` / `DatabaseNamesFor` / `ProviderDeclaresType`, driven by `IsFamilyDefault` + `ProviderFamily` |
| "film", "tv_episode", "track" ... mapped in a switch | `media_types.Aliases`; a client's name resolves to the type that lists it |
| which types are movie-like / show-like decided by name | `ProviderFamily` (`MediaTypeRegistry.IsMovieLike` / `IsShowLike`) |
| `anime`, `books`, `audiobooks`, `music` selecting import and scan code paths | `media_types.ImportShape` (`episodic`, `author-series-work`, `artist-album-track`) and `ScanStrategy` |
| `Cast` vs `Band Members` vs `Narrators` and "cast are actors" by type name | `CastHeading`, `CastAreActors` |
| a root id's entity type checked against type names (`"artist"` -> `music`, `"movie"` -> `movies/fanedits/anime`) | the provider's declared id shapes against the item's provider family (below) |
| `"books"` / `"audiobooks"` same-work pair and `music` level-2 shared ids as code defaults | two `app_settings` rows seeded by the migration |
| search endpoints defaulting `mediaTypeHint = "movie"` | `MediaTypeRegistry.DefaultProviderName` (the default type's provider name) |
| scanner confidence setting read from the plugin named `chronicle.plugin.filescanner` | read from whichever scanner serves the type |

Plugins carry the same facts for the types they create (`MediaTypeSupport.ProviderFamily`, `CastHeading`, `CastAreActors`,
`Aliases`, `IsPersonType`, `ImportShape`), filled in once when the type is first created. After that an administrator's
edit wins. Documented in `docs/MEDIA_TYPES.md`.

### Provider knowledge belongs to the provider

New optional interface `IExternalIdPolicyProvider` (Chronicle.Plugins) returning an `ExternalIdPolicy`; Chronicle reads them
through `ExternalIdPolicies` (refilled whenever plugins load or unload). Documented in `docs/PLUGIN_AUTHORING.md`.

| Was in Chronicle | Now declared by |
|---|---|
| `imdb:` prefix stored bare, `nm...` person ids keeping it | IMDb (`SharedRawIdPrefix`, `KeepSharedPrefixWhenIdStarts`) |
| TMDB `person:N` stored as `tmdb:N` | TMDB (`StoredPrefixRewrites`) |
| untyped `simkl:N` refused (helper class + 9 tests) | Simkl (`RefusedIdPattern`) |
| `fanarttv` skipped in conflict checks | FanartTV (`IgnoredForConflicts`) |
| `wikipedia` excluded as merge proof; `chronicle.plugin.wikipedia` + `"wikipedia"` in three repair services; default title-cleanup target | Wikipedia (`NameDerived`, `DisambiguatedTitleField`, `BirthYearTagPattern`, `NotAnEntityTitlePattern`); the repair services read the provider and its patterns from the policy |
| cross-reference formats (`movie:N`, `tv:N`, `trakt:movie:N`, `simkl:anime:N` ...) in `CrossRefHelper`; `isAnime` from the type name | each provider (`CrossRefFormats`, tried by "movie" then by the item's type name) |
| TMDB/MusicBrainz id grammar in the enrichment step (`movie`, `tv`, `artist`, `release-group`, `recording`, depth rules, "movie inside a collection") | TMDB and MusicBrainz (`IdShapes`: shape, depth, item family, parent family) |
| building `tv:{id}/season:{n}/episode:{m}` from a show's id (three places) | TMDB (`ChildIds`) |
| reading season/episode back out of an id (regex in `FileIdentityJson`) | TMDB (`PositionInIdPattern`) |
| "this id is a movie id" sniffed from `movie:` / `:movie:` | TMDB, Simkl, Trakt (`FamilyIdPatterns`) |
| `MovieExternalIdRepairService` believing TMDB, its `movie:N` shape, its `imdb`/`tvdb` cross-references | the item's authority = first provider in the Metadata Assignment precedence for its type that has data on it; "movie id" from that provider's `FamilyIdPatterns`; cross-references compared only when written alike |
| "YYYY births" tag and "YYYY in film" title shapes inside the Wikipedia repair services | Wikipedia (`BirthYearTagPattern`, `NotAnEntityTitlePattern`); the services keep only the splitting mechanics |
| Hardcover series reconcile hard-wired to `"hardcover"` and chained to it in the task scheduler | Hardcover (`SeriesIdMarker`); the service is now `SeriesReconcileService`, parameterised by plugin id |
| TMDB image host test when clearing a stale child poster | removed: a child with no poster in the new result drops its poster and the resolver re-promotes whatever remains |

Six shipped plugins (TMDB, IMDb, Trakt, Simkl, FanartTV, Wikipedia) plus MusicBrainz and Hardcover declare policies; the
repositories are committed locally and not released.

### Tunable numbers are settings

17 numbers that were constants inside services are declared once in `Tunables` (default, range, unit, description), stored as
`tuning.*` in `app_settings`, editable at **Settings -> Tuning** (admins), and take effect within a minute: watched
threshold (80%), now-playing window, watch-session gap, enrichment retry window, minimum match confidence, person
full-credits age, year-conflict gap, book-series recheck / per-run / failure-retry / failures-to-stop, interrupted-fetch
retry, image address trust and re-check times, Kodi scan-signal lifetime, wrong-type-folder majority / minority shares. An
unreadable or out-of-range stored value is ignored, so a typo cannot disable a rule.

### Configuration written twice or into code

* The built-in plugin catalogue (20 entries) duplicated `plugins.json`; it is now that file embedded in the build.
* Three copies of the audio extension list, two of the video list: one `MediaFileExtensions` (the administrator's extras still add to it).
* The `User-Agent` string written twice in `Program.cs`: one constant.
* Defaults kept on purpose: the catalogue URL (`plugins.catalog_url` overrides it), the dev CORS origin (derived from `ports.json`).

## Remaining, and why

| Where | What | Why it stays |
|---|---|---|
| `ProviderFamilies` (`movie`, `tv`, `music`) | family vocabulary | It is the vocabulary providers and Kodi speak; types and providers both refer to it. |
| `ScraperController` (`imdb`, `tvdb`, `tmdb`, `trakt` keys) | Kodi `uniqueid` types and the scraper payload | The Kodi add-on's contract, not Chronicle's choice. |
| `BuiltInMediaTypes` | `tv`, `movies`, `music`, `people` | The seed. Rows are ordinary afterwards. |
| `LegacyNfoPurgeService` | the retired Kodi NFO plugin id and partition names | One-shot, marker-gated upgrade of data from a feature that no longer exists. Delete once every install has run it. |
| Migrations | type names in data fixes | History. |
| Constants such as page sizes, timeouts, `MaxEntries`, `PasswordHasher.WorkFactor`, `MaxSessionsPerUser` | technical limits | Not policy. |

## Redundancy and dead code

**Duplicates collapsed:** provider-name normalisation (5 copies -> 1), external-id source/normalisation (enrichment, file scan,
conflict check, three repair services -> `ExternalIdPolicies`), plugin catalogue (code and file -> file), audio/video
extension lists (4 -> 1), `User-Agent` (2 -> 1), `"hardcover"` task chain (special case -> declaration), two copies of the
season/episode id grammar.

**Removed as dead:**

* API: the flat scan endpoints `POST /scan`, `/scan/preview`, `/scan/identify`, `/scan/import`, `/scan/import-direct` (the web app and the Kodi add-ons use the grouped ones), with `ScanAsync`, `PreviewAsync`, `IdentifyAsync`, `ImportApprovedAsync`, `ImportDirectAsync`, five private helpers (`ImportHierarchicalAsync`, `FindExistingItemAsync`, `CreateStubItemAsync`, `ScoreCandidate`, `ScoreByNameYear`, `ImportSingleFileAsync`) and about twenty request/response records; `FixWrongActorWikipediaMatches`, `normalize-tmdb-person-ids`, `dedupe-tmdb-duplicates`, `strip-wikipedia-disambiguation-suffix`, `wikipedia-collision-candidates` (one-time clean-ups of bugs fixed at the root, no caller anywhere).
* Services: `BuiltInFileScannerPlugin` (never registered; only its own tests used it), `PluginLoggerFactory` (same), `SyncProgressService`, `SimklIdHelper`, `MediaTypeRegistry.NamesInFamily` / `IdsInFamily`, `MetadataEnrichmentService.NormalizeSearchName`, the Trakt-only credit backfill at startup, `MediaTypeRules.SuggestedVerbs`, an unused raster list in `PluginsController`.
* Core: `MediaItemRefreshLog` (a model with no table mapping).
* Web client: unused exports in `api/database`, `api/duplicates`, `api/import`, `api/media`, `api/reports`, `api/scan`, `api/deviceAuth` and the scan-result types.
* A statically-detected "unused" list also named eight `api/plugins.ts` functions; they are used by the Plugins page (the detector missed a destructured import), so they stayed. Compilation caught it.

Left alone because they are contracts, not dead: `ScannedFile` fields plugins fill in, EF interceptor overrides, `ChronicleDbContextFactory` (design-time), controller actions only Kodi or scripts call.

## Tests

`ExternalIdPolicies`: 56 cases (normalisation, refusals, cross-reference formats, id-shape validity by depth and family, child-id
templates, positions, family patterns); `Tunables`: 7; `MediaTypeRegistry` and media-type sync cases extended. Unit 2,039
pass, web 299 pass, integration 559 pass / 9 skipped (run on a clean copy), and one policy test in each of the eight plugin repositories. Two integration hazards found and fixed on the way: the startup
settings read cast the injected settings to the concrete class (47 failures in the test host), and parallel hosts overwrote
each other's process-wide media-type snapshot (`MediaTypeRegistry.IsolateForCurrentContext`, `PreserveExecutionContext`).

## Behaviour changes to know about

* A TMDB `movie:N` id on a *tv-family* item (e.g. an `anime` root) is now judged implausible, as `tv` always was; previously `anime` was exempted by name. The live database has none (checked).
* Stored ids are judged only by their own provider's shapes. A provider that declares none is trusted, as unrecognised ids always were.
* The startup Trakt credit backfill is gone; `QueueCachedCreditsBackfill` already resolves credits from cached metadata.
* A new migration (`MediaTypeDataDrivenFlags`, both SQLite and PostgreSQL sets) adds the media-type columns, converts existing rows, and seeds the two enrichment settings. It was applied to the live database on the API restart at 14:28 on 2026-10-16.
* **Core and plugins now go together.** Chronicle no longer carries the rules the policies replace, so a released Chronicle needs the released plugins that declare them (TMDB, IMDb, Trakt, Simkl, FanartTV, Wikipedia, MusicBrainz, Hardcover). Without TMDB's policy, for example, `person:N` ids would be stored as issued again. `RunTestEnvironment.ps1` rebuilds the plugins from their repositories, so the development install already has them; the plugin releases have not been cut.
