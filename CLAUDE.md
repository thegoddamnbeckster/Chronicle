# Chronicle — CLAUDE.md

Self-hosted universal media tracking platform. Tracks movies, TV, music, and any custom media type through a flexible plugin architecture.

**Repo:** `thegoddamnbeckster/Chronicle`
**Status:** Pre-implementation (docs complete, Phase 1 MVP next)
**Owner:** Chronicle Contributors — PowerShell/Python background, not a C# developer

---

## Tech Stack

- **Backend:** .NET Core 8.0 / ASP.NET Core / Entity Framework Core / Kestrel
- **Frontend:** React 18 + TypeScript
- **Database:** SQLite (default), PostgreSQL (production option)
- **Auth:** JWT (web/mobile) + API keys (scrobblers)
- **API:** REST, versioned at `/api/v1/`, Swagger at `/swagger`

---

## Project Structure

```
src/
├── Chronicle.Core/           # Domain models, interfaces — NO business logic
├── Chronicle.Data/           # EF Core DbContext, repositories, migrations
├── Chronicle.Services/       # Business logic, service layer
├── Chronicle.API/            # ASP.NET Core controllers, middleware
├── Chronicle.Plugins/        # Plugin interfaces (IMetadataProvider, IWidgetPlugin, IMediaTypePlugin)
├── Chronicle.Plugins.TMDB/   # Reference plugin implementation
├── Chronicle.Updater/        # Separate update process
└── Chronicle.Web/            # React frontend

tests/
├── Chronicle.Tests.Unit/
├── Chronicle.Tests.Integration/
└── Chronicle.Tests.E2E/
```

---

## Architecture Rules

1. **Plugin-first** — All media type support and metadata scraping goes through plugin interfaces. Nothing hardcoded.
2. **Generic data model** — No type-specific tables. `media_types`, `media_items`, and `media_groups` use JSON metadata columns for type-specific fields.
3. **Layer separation** — Domain models/interfaces in Core. Data access in Data. Business logic in Services. HTTP concerns in API only.
4. **Async everywhere** — All I/O operations use async/await. Suffix with `Async`.
5. **Stateless plugins** — Plugins don't store state between calls. Use settings and the database.

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

Migrations are sequential SQL files with up/down scripts. Track version in `schema_version` table.

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

```bash
# Backend
cd src/Chronicle.API && dotnet restore && dotnet build
cd src/Chronicle.API && dotnet run                        # Starts on :8080
cd tests && dotnet test --verbosity normal

# Frontend
cd src/Chronicle.Web && npm install
cd src/Chronicle.Web && npm run dev                       # Starts on :3000
cd src/Chronicle.Web && npm run lint
cd src/Chronicle.Web && npm run type-check

# Database
dotnet ef database update                                 # Apply migrations
dotnet ef migrations add MigrationName                    # Create migration
```

---

## Testing Requirements

- Unit tests: 80%+ coverage target
- Integration tests for critical paths (scrobble flow, auth, plugin loading)
- Test files alongside source with `.test.ts` (frontend) or in `tests/` projects (backend)
- Never commit code without corresponding tests

---

## Phase 1 MVP Scope (v0.1–v0.5)

1. Project scaffolding and solution structure
2. Database foundation (SQLite, EF Core, migrations)
3. Authentication (JWT + API keys)
4. Media management (CRUD, search, metadata via TMDB plugin)
5. Scrobbling (receive, process, store, currently-watching)
6. Basic statistics
7. React frontend (dashboard, library, media detail, scrobble history)
8. Windows deployment packaging

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
