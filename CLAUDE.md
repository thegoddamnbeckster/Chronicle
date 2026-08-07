# Chronicle — CLAUDE.md

Self-hosted universal media tracking platform. Tracks movies, TV, music, and any custom media type through a flexible plugin architecture.

**Repo:** `thegoddamnbeckster/Chronicle`
**Status:** Active development — v0.7.0 released. Well past MVP: media/collection management,
file scanner, 12 plugins, scrobbling, sync, merge/dedup, metadata assignment, image overrides.
**Owner:** Chronicle Contributors — PowerShell/Python background, not a C# developer

---

## Tech Stack

- **Backend:** .NET 9 / ASP.NET Core / Entity Framework Core / Kestrel
- **Frontend:** React 18 + TypeScript
- **Database:** SQLite (default), PostgreSQL (production option)
- **Auth:** JWT (web/mobile) + API keys (scrobblers)
- **API:** REST, versioned at `/api/v1/`, Swagger at `/swagger`

---

## Project Structure

```
src/
├── Chronicle.Core/           # Domain models, helpers, interfaces — NO business logic
├── Chronicle.Data/           # EF Core DbContext + migrations
├── Chronicle.Services/       # Business logic, service layer
├── Chronicle.API/            # ASP.NET Core controllers, middleware
├── Chronicle.Plugins/        # Plugin interfaces (IMetadataProvider, IImportProvider, IWidgetPlugin, IThemePlugin)
└── Chronicle.Web/            # React frontend

tests/
├── Chronicle.Tests.Unit/
└── Chronicle.Tests.Integration/
```

`src/Chronicle.Plugins.TMDB/` exists on disk but is NOT in the solution — it's a vestigial
early reference implementation. The real TMDB plugin is the sibling repo described below.

### Plugins live in their own repos

Plugins are NOT part of this repo and are NOT bundled into Chronicle's releases. Each is a
sibling directory (`W:\Scripts\Chronicle.Plugin.*`) with its own GitHub repo, its own version
number, and its own releases — users download only the ones they want. As of v0.7.0: TMDB,
MusicBrainz, FileScanner, FanEdit, MoviesRemastered, Simkl, Trakt, Hardcover, FanartTV,
TheTVDB, TVMaze, Themes.Default.

Chronicle's own releases ship source/tag only, with no attached build artifacts.

`scripts/RunTestEnvironment.ps1` rebuilds every plugin from its sibling directory and deploys
the DLLs into `src/Chronicle.API/plugins/` — that's why plugin code changes need that script,
not just a `dotnet build` of the API.

---

## Architecture Rules

1. **Plugin-first** — All media type support and metadata scraping goes through plugin interfaces. Nothing hardcoded.
2. **Generic data model** — No type-specific tables. `media_types`, `media_items`, and `media_groups` use JSON metadata columns for type-specific fields.
3. **Layer separation** — Domain models/interfaces in Core. Data access in Data. Business logic in Services. HTTP concerns in API only.
4. **Async everywhere** — All I/O operations use async/await. Suffix with `Async`.
5. **Stateless plugins** — Plugins don't store state between calls. Use settings and the database.
6. **Lossless ingestion** — Chronicle stores everything it receives. Every field from every scrobbler payload, metadata provider response, and file scanner result must be persisted — nothing is silently discarded. Fields that don't map to first-class schema columns go into the item's `metadata_json` column, partitioned by source (e.g. `{"tmdb": {...}, "fileScanner": {...}}`). This guarantees that data is never lost at the point of ingestion and can be surfaced or re-processed later without a re-fetch.

---

## Key Interfaces

```csharp
// Metadata scraping — all plugins implement this
public interface IMetadataProvider
{
    string Name { get; }
    string Version { get; }
    MediaTypeSupport[] GetSupportedMediaTypes();
    PluginSettingsSchema GetSettingsSchema();
    Task<MediaMetadata> SearchAsync(string query);
    Task<MediaMetadata> GetByIdAsync(string id);
    Task<byte[]> GetImageAsync(string url);
    Task<bool> HealthCheckAsync();
}

// Dashboard widgets
public interface IWidgetPlugin
{
    string WidgetType { get; }
    string DisplayName { get; }
    List<SettingDefinition> GetSettings();
    Task<WidgetData> RenderAsync(WidgetSettings settings);
}
```

---

## Database Design (Key Tables)

- `users` — Accounts, bcrypt passwords (cost 12), JSON preferences
- `media_types` — Configurable types with hierarchy levels, interaction verbs, progress units
- `media_groups` — Groups versions of same media (e.g., Blade Runner theatrical vs Director's Cut)
- `media_items` — Individual media; hierarchical via `parent_id` (show→season→episode)
- `media_external_ids` — Cross-references to TMDB, IMDB, TVDB, MusicBrainz
- `interaction_events` — Every scrobble/watch/listen event
- `user_libraries` — User's tracked media with status (watching, completed, dropped, etc.)
- `watch_sessions` — Rewatch session grouping
- `plugins` — Installed plugins with encrypted settings JSON
- `app_settings` — Global key-value config

Migrations are EF Core code-first migrations (`src/Chronicle.Data/Migrations/*.cs`), applied
with `dotnet ef database update`. There is no hand-written SQL migration set and no
`schema_version` table — EF's own `__EFMigrationsHistory` tracks what's applied.

---

## API Patterns

**Response envelope:**
```json
{ "success": true, "data": {...}, "pagination": {...} }
{ "success": false, "error": { "code": "MEDIA_NOT_FOUND", "message": "..." } }
```

**Auth:** `Authorization: Bearer {jwt}` for web, `X-API-Key: chr_live_...` for scrobblers.

**Key endpoints:** `/api/v1/scrobble`, `/api/v1/media/search`, `/api/v1/media/{id}`, `/api/v1/users/me`, `/api/v1/auth/login`, `/api/health`

---

## C# Conventions

- Microsoft C# Coding Conventions + `.editorconfig`
- PascalCase public members, `_camelCase` private fields
- 4-space indentation
- Constructor injection for dependencies
- Custom exceptions per domain (e.g., `MediaNotFoundException`)
- Log all operations, each plugin gets its own log file

## TypeScript/React Conventions

- Functional components with hooks
- Strict TypeScript (no `any`)
- UI aesthetic matches Sonarr/Radarr

---

## Git Workflow

- **Branches:** `main` → `develop` → `feature/*`, `bugfix/*`, `hotfix/*`, `release/*`
- **Commits:** Conventional Commits — `feat(scope): message`, `fix(scope): message`
- **Merge:** Squash-and-merge for features, merge commit for releases
- **Always branch from `develop`**, merge back to `develop`, release merges to `main`

---

## Commands

**Start the dev environment with the script, not `dotnet run`:**

```powershell
.\scripts\RunTestEnvironment.ps1                # API + web + ABS bridge, each in its own window
.\scripts\RunTestEnvironment.ps1 -ApiOnly       # API only
```

It kills stale processes, rebuilds and redeploys all 12 plugin DLLs, then starts everything.
Running `dotnet run` by hand starts only the API with stale plugins — and leaves the frontend
down, which looks exactly like "I can't log in".

Ports come from `ports.json` at the repo root (the single source of truth, read by both
`vite.config.ts` and the API): **API 7979, web 8888.** The ABS bridge runs on 9877.

```bash
# Backend
cd src/Chronicle.API && dotnet restore && dotnet build
cd tests && dotnet test                                   # both suites

# Frontend
cd src/Chronicle.Web && npm install
cd src/Chronicle.Web && npm run lint
cd src/Chronicle.Web && npx tsc --noEmit                  # type-check
cd src/Chronicle.Web && npm run build

# Database (EF Core code-first)
dotnet ef database update                                 # Apply migrations
dotnet ef migrations add MigrationName                    # Create migration
```

**Build gotcha:** `dotnet build` fails with MSB3027/MSB3021 file locks while Chronicle.API is
running. Stop it first (`Get-Process Chronicle.API | Stop-Process -Force`), or just use
RunTestEnvironment.ps1, which handles the kill-build-start ordering itself.

---

## Testing Requirements

- Unit tests: 80%+ coverage target
- Integration tests for critical paths (scrobble flow, auth, plugin loading)
- Test files alongside source with `.test.ts` (frontend) or in `tests/` projects (backend)
- Never commit code without corresponding tests

---

## Artwork overrides (image pinning)

Any image Chronicle knows about can be pinned into any of the 8 canonical artwork slots
(`poster_url`, `backdrop_url`, `logo_url`, `banner_url`, `thumb_url`, `clearart_url`,
`disc_url`, `character_art_url`). Key invariants:

- Pins live in `media_items.MetadataJson` under the reserved top-level `_overrides` key —
  sibling to `_resolved`, no separate table. Any code that rewrites MetadataJson must
  preserve it.
- `MetadataResolutionService.ResolveAsync` checks `_overrides[field]` **before** the
  plugin-priority walk, so a pin wins over every provider and survives refresh, merge, sync,
  and bulk recompute — all of which funnel through ResolveAsync. It stays until cleared.
- An image's source type only decides how it's grouped for browsing, not what it can become:
  a backdrop can be pinned as a poster, and one image can hold several slots at once.
- Assignment UI lives **only** inside the full-size image viewers, never on the detail page.
- Reset is available at five scopes: one slot, one item, one item + all descendants
  (collections/shows), one media type, and library-wide.

---

## Documentation Reference

Full specs live in `docs/` — consult before implementing any feature:

- `ARCHITECTURE.md` — System design, data flows, scalability
- `DATABASE_SCHEMA.md` — Complete table definitions with examples
- `API_SPECIFICATION.md` — All endpoints, request/response formats
- `PLUGIN_SYSTEM.md` — Plugin interfaces, examples, best practices
- `FEATURES.md` — Detailed feature requirements
- `SECURITY.md` — Auth, encryption, rate limiting
- `UI_DESIGN.md` — Interface design, Sonarr/Radarr aesthetic
- `LOGGING.md` — Logging system design
- `DEPLOYMENT.md` — Build, package, update system
- `DEVELOPMENT.md` — Full contributor guide, PR process
