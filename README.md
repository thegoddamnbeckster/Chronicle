# Chronicle

**Universal Media Tracking Platform**

Chronicle is a self-hosted, open-source media tracking application that lets you track any type of media — movies, TV shows, music, books, podcasts, and more. Built with privacy, extensibility, and user control as core principles.

---

## Project Status

**Current Phase:** Phase 2 — Core Features (active development)
**Current Version:** v0.3.0
**Target v1.0:** Q4 2026

---

## What's Built

### Core Platform
- **Authentication** — JWT for web/mobile, API key auth for scrobblers (`chr_live_...` prefix)
- **User management** — Registration, login, preferences; first user auto-promoted to admin
- **REST API** — Versioned at `/api/v1/`, full Swagger UI at `/swagger`
- **SQLite database** — EF Core 9 with sequential migration files

### Media Management
- **Universal media model** — No type-specific tables; `media_types`, `media_items`, and `media_groups` with JSON metadata columns
- **Hierarchical items** — Show → Season → Episode, Artist → Album → Track (arbitrary depth)
- **Library tracking** — Per-user status (Watching/Completed/Dropped/On Hold/Plan to Watch), custom ratings
- **Context-aware verbs** — "Plan to Listen" for music, "Plan to Read" for books, "Plan to Watch" for video
- **Search & CRUD** — Full media search, create/update/delete

### File Scanner
- **Multi-signal hierarchical grouping** — Combines folder names, embedded tags (via TagLib#), and NFO sidecar files to group files into Artist→Album→Track or Show→Season→Episode trees
- **Confidence scoring** — Each group scored 0–100%; users can review and accept/reject before importing
- **Year extraction** — Reads `(YYYY)` from folder names (e.g. `Star Trek, Enterprise (2001)`) even when embedded tags use a different name
- **Episode/track number extraction** — Parses `S02E05`, `01 - Track Name`, leading numbers from filenames
- **Sidecar exclusion** — Ignores `theme-music`, `.actors`, `extrafanart`, `behind the scenes`, etc.
- **Import progress bar** — Background task with 500ms polling; shows current group name and % complete
- **Configurable batch size** — DB commits every N groups (default 50, configurable in Settings)
- **Deduplication** — Matches existing items by folder path, falls back to name (strips year suffixes)

### Metadata — TMDB Plugin
- **Movie & TV search** — Full-text search with year scoring
- **Season metadata** — Fetches per-season posters, overview, air date via `/tv/{id}/season/{n}`
- **Episode metadata** — Fetches episode titles, stills, overviews, air dates, guest cast via `/tv/{id}/season/{n}/episode/{e}`
- **Background auto-refresh** — Nightly at 1am; cascades from root show → seasons → episodes
- **Fix Match / Clear Match** — Manual override per item
- **TMDB suppression** — Hide box entirely for non-supported types (Music, Books, etc.)

### Background Tasks
- **Configurable scheduler** — Visual cron editor in Settings; Run Now button
- **Metadata refresh** — Nightly at 1am, full hierarchy refresh with TMDB rate-limit handling (250–300ms delay)
- **Duplicate cleanup** — Nightly at 3am

### React Frontend
- **18+ pages** — Dashboard, Library, Media Detail, File Scan (3-step), Settings, Background Tasks, Auth
- **Sonarr/Radarr aesthetic** — Dark teal theme
- **Library sections** — Grouped by media type with Prev/Next pagination and Show All
- **Media detail** — TMDB box (rating, genres, cast, images, air date, episode count), File Scanner box (path/import date), breadcrumb navigation (↑ parent button at season/episode level)
- **File Scan wizard** — Grouped ScanGroupCard UI, confidence badges, accept/reject, live import progress
- **Connecting indicator** — Login/register pages wait for API to be ready before enabling form
- **Diagnostic footer** — Expandable panel with system info

---

## Technology Stack

| Layer | Technology |
|-------|-----------|
| Backend | .NET 9 / ASP.NET Core / Kestrel |
| ORM | Entity Framework Core 9 |
| Database | SQLite (default) / PostgreSQL |
| Auth | JWT + API Keys |
| Metadata | TMDB plugin (implements `IMetadataProvider` + `ITvDetailProvider`) |
| Audio tags | TagLibSharp |
| Frontend | React 18 + TypeScript (strict) |
| Styling | CSS Modules |
| HTTP client | Axios + TanStack Query |
| Cron | Cronos |

---

## Quick Start (Windows)

```powershell
# Clone and build
git clone https://github.com/thegoddamnbeckster/Chronicle.git
cd Chronicle

# Start API (port 8080)
cd src/Chronicle.API
dotnet run

# Start frontend dev server (port 3000) — separate terminal
cd src/Chronicle.Web
npm install
npm run dev
```

Open `http://localhost:3000` — the first account you register is automatically admin.

---

## Project Structure

```
src/
├── Chronicle.Core/           # Domain models, exceptions — no business logic
├── Chronicle.Data/           # EF Core DbContext, repositories, migrations
├── Chronicle.Services/       # All business logic
│   └── Scan/                 # Hierarchical file scanner pipeline
├── Chronicle.API/            # ASP.NET Core controllers, DTOs, middleware
├── Chronicle.Plugins/        # Plugin interfaces (IMetadataProvider, ITvDetailProvider, ...)
├── Chronicle.Plugins.TMDB/   # TMDB reference plugin
└── Chronicle.Web/            # React 18 + TypeScript frontend

tests/
├── Chronicle.Tests.Unit/       # 138 passing
└── Chronicle.Tests.Integration/ # 91 passing
```

---

## Roadmap

### Phase 1: MVP — Complete ✅
- Core API (auth, users, media CRUD, scrobble endpoint)
- SQLite + EF Core migrations
- JWT + API key authentication
- React frontend (dashboard, library, media detail)
- Windows executable packaging

### Phase 2: Core Features — In Progress 🔄
- ✅ Hierarchical file scanner (Show→Season→Episode, Artist→Album→Track)
- ✅ TMDB plugin (movies, TV shows, seasons, episodes)
- ✅ Background metadata refresh (nightly, full hierarchy)
- ✅ Import progress bar
- ✅ Context-aware interaction verbs (Listen/Read/Watch/Play)
- 🔲 MusicBrainz plugin
- 🔲 Kodi/Plex scrobbler integration
- 🔲 Rewatch session tracking UI
- 🔲 Docker support

### Phase 3: Advanced Features
- 🔲 Multi-user library sharing
- 🔲 Import from Trakt/SIMKL
- 🔲 Custom media types via UI
- 🔲 Mobile-responsive layout

### Phase 4: Ecosystem
- 🔲 Native mobile apps
- 🔲 Advanced analytics
- 🔲 Community plugin marketplace

---

## API Overview

```
POST /api/v1/auth/login          # Get JWT
POST /api/v1/auth/register
GET  /api/v1/media/search        # Search all media
GET  /api/v1/media/{id}          # Media detail + TMDB metadata
POST /api/v1/scrobble            # Record a watch/listen event
GET  /api/v1/users/me            # Current user profile
POST /api/v1/scan/preview-grouped  # Hierarchical scan preview
POST /api/v1/scan/import-groups    # Import (202 + background)
GET  /api/v1/scan/import-progress  # Poll import progress
GET  /api/health                 # Health check
GET  /swagger                    # Interactive API docs
```

Auth: `Authorization: Bearer {jwt}` for web, `X-API-Key: chr_live_...` for scrobblers.

---

## Contributing

See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for coding standards, git workflow, and PR process.

**Branches:** `main` → `develop` → `feature/*`
**Commits:** Conventional Commits (`feat(scope): message`)
**Tests:** 80%+ coverage target; never commit without tests

---

## Credits

**Design, direction & testing:** Michael Beck
**Implementation:** Anthropic Claude (AI Assistant)
**License:** MIT

---

## License

MIT License — see [LICENSE](LICENSE) file for details.

---

**Chronicle** — Your media, your data, your way.
