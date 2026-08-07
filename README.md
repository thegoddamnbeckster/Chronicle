# Chronicle

**Universal Media Tracking Platform**

Chronicle is a self-hosted, open-source media tracking application that lets you track any type of media — movies, TV shows, music, books, podcasts, audiobooks, anime, fan edits, and more. Built with privacy, extensibility, and user control as core principles.

---

## Project Status

**Current Phase:** Active development
**Current Version:** v0.7.1
**Target v1.0:** Q4 2026

---

## What's Built

### Core Platform
- **Authentication** — JWT for web/mobile, API key auth for scrobblers (`chr_live_...` prefix)
- **User management** — Registration, login, preferences, fold state; first user auto-promoted to admin
- **REST API** — Versioned at `/api/v1/`, full Swagger UI at `/swagger`
- **SQLite database** — EF Core 9 with sequential migration files
- **Plugin system** — Isolated `PluginLoadContext` per plugin; supports metadata, import, widget, and report plugin types; hot-reload without API restart

### Media Management
- **Universal media model** — No type-specific tables; `media_types`, `media_items`, and `media_groups` with JSON metadata columns
- **Hierarchical items** — Show → Season → Episode, Artist → Album → Track (arbitrary depth via `HierarchyLevels`)
- **Library tracking** — Per-user status (Watching/Completed/Dropped/On Hold/Plan to Watch), custom ratings, watch events
- **Context-aware verbs** — "Plan to Listen" for music, "Plan to Read" for books, "Plan to Watch" for video
- **Search & CRUD** — Full media search, create/update/delete, credits (cast/crew)
- **Global search** — Header search bar with debounced results, poster thumbnails, click-to-navigate

### File Scanner
- **Multi-signal hierarchical grouping** — Combines folder names, embedded tags (via TagLib#), and NFO sidecar files to group files into Artist→Album→Track or Show→Season→Episode trees
- **Audiobook support** — Groups audio files by book folder; parses `Series - N - (Year) - Title` format; reads AudioAlbum, AudioGrouping, and Author tags; stores author for enrichment
- **Confidence scoring** — Each group scored 0–100%; users can review and accept/reject before importing
- **Year extraction** — Reads `(YYYY)` from folder names even when embedded tags use a different name
- **Episode/track number extraction** — Parses `S02E05`, `01 - Track Name`, leading numbers from filenames
- **Deduplication** — Matches existing items by folder path, then title+year (with colon/dash variant matching)
- **Import progress** — Background task with live polling; shows current group and % complete

### Metadata Enrichment
- **Unified enrichment service** — `MetadataEnrichmentService` with pluggable providers; per-item status tracking (Pending/Completed/NotFound/Failed/Skipped/Exhausted)
- **Hierarchical search cascade** — 4-stage search: `AltTitles` → parent/child hints → sub-item metadata → fallback
- **Fix Match / Clear Match** — Manual override per item per plugin
- **Drill-down page** — Settings → Enrichment: per-status filtered view, bulk reset, live polling
- **Metadata Assignment** — Settings → Metadata Assignment: configure which plugin provides each field per media type, with priority ordering; display names and available plugins come from the DB+registry
- **Parent-type inheritance** — Anime inherits TV providers; Fan Edits inherit Movie providers

### Inbound Sync (Trakt & SIMKL)
- **`SyncOrchestrationService`** — 4-stage item matching (ExternalId → cross-ref AdditionalIds → title+year → create stub); deduplicates watch events; delta sync via stored `last_synced_at`
- **Watch history, ratings, watchlist** — All synced per plugin
- **Credits** — Cast and director credits synced from Trakt

### Installed Plugins

| Plugin | Type | Media Types | Release |
|--------|------|-------------|---------|
| **[TMDB](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB)** | Metadata | Movies, TV, Anime, Fan Edits, Seasons, Episodes | [![](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.TMDB?label=&color=01b4e4)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB/releases/latest) |
| **[MusicBrainz](https://github.com/thegoddamnbeckster/Chronicle.Plugin.MusicBrainz)** | Metadata | Music (albums, artists), Audiobooks | [![](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.MusicBrainz?label=&color=ba478f)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.MusicBrainz/releases/latest) |
| **[Trakt](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Trakt)** | Import/Sync + Metadata | Movies, TV | [![](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.Trakt?label=&color=ed1c24)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Trakt/releases/latest) |
| **[SIMKL](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Simkl)** | Import/Sync + Metadata | Movies, TV, Anime | [![](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.Simkl?label=&color=0c9a40)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Simkl/releases/latest) |
| **[FanEdit (IFDB)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FanEdit)** | Metadata | Fan Edits (scrapes fanedit.org; requires account) | [![](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.FanEdit?label=&color=6f42c1)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FanEdit/releases/latest) |
| **[Hardcover](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Hardcover)** | Import/Sync + Metadata | Books, Audiobooks | [![](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.Hardcover?label=&color=a0522d)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Hardcover/releases/latest) |
| **[File Scanner](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FileScanner)** | Scanner | All (local files) | [![](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.FileScanner?label=&color=4f72c4)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FileScanner/releases/latest) |
| **[Fanart.tv](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FanartTV)** | Artwork | Movies, TV, Anime, Fan Edits, Music | [![](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.FanartTV?label=&color=F5A623)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FanartTV/releases/latest) |

### React Frontend (20+ pages)
- **Sonarr/Radarr aesthetic** — Dark teal/green theme
- **Library** — Grouped by media type, Prev/Next pagination, physical-file vs metadata-only icons
- **Media Detail** — Plugin metadata boxes (collapsible, server-persisted fold state), breadcrumb navigation, credits, fix-match panel
- **File Scan wizard** — 3-step: configure → preview (grouped cards with confidence badges, series/author display) → import
- **Background Tasks** — Visual cron editor, Run Now, grouped by plugin, live running state
- **Settings** — App settings, service status, Metadata Assignment (per-type per-field plugin priority), plugin management
- **Enrichment drill-down** — Per-status tab, search (covers name/author/series/external ID), bulk reset

---

## Technology Stack

| Layer | Technology |
|-------|-----------|
| Backend | .NET 9 / ASP.NET Core / Kestrel |
| ORM | Entity Framework Core 9 |
| Database | SQLite |
| Auth | JWT + API Keys |
| Audio tags | TagLibSharp |
| Frontend | React 18 + TypeScript (strict) |
| Styling | CSS Modules |
| HTTP client | Axios + TanStack Query |
| Cron | Cronos |

---

## Quick Start (Windows)

See [docs/SETUP.md](docs/SETUP.md) for full new-machine setup instructions including plugin deployment.

```powershell
# Clone
git clone https://github.com/thegoddamnbeckster/Chronicle.git
cd Chronicle

# API (port 7979)
cd src\Chronicle.API
dotnet run

# Frontend dev server (port 8888) — separate terminal
cd src\Chronicle.Web
npm install
npm run dev
```

Open `http://localhost:8888`. The first account you register is automatically admin.

Create `src\Chronicle.API\appsettings.Development.json` with your secrets (this file is `.gitignore`d):

```json
{
  "Security": { "JwtSecret": "your-64-char-secret" },
  "GitHub": { "Token": "your-github-pat" },
  "Urls": "http://localhost:7979"
}
```

---

## Project Structure

```
src/
├── Chronicle.Core/       # Domain models, exceptions — no business logic
├── Chronicle.Data/       # EF Core DbContext, migrations
├── Chronicle.Services/   # All business logic (enrichment, scan, sync, library, …)
├── Chronicle.API/        # ASP.NET Core controllers, DTOs, middleware
├── Chronicle.Plugins/    # Plugin interfaces (IMetadataProvider, IImportProvider, …)
└── Chronicle.Web/        # React 18 + TypeScript frontend

tests/
├── Chronicle.Tests.Unit/         # 264 passing
└── Chronicle.Tests.Integration/  # 123 passing
```

---

## Roadmap

### Phase 1: MVP — Complete ✅
- Core API, SQLite, JWT + API key auth, React frontend, Windows packaging

### Phase 2: Core Features — In Progress 🔄
- ✅ Hierarchical file scanner (Show→Season→Episode, Artist→Album→Track, Audiobooks)
- ✅ TMDB plugin (movies, TV, anime, fan edits, seasons, episodes)
- ✅ MusicBrainz plugin (albums, artists, audiobooks)
- ✅ Background metadata enrichment (nightly, full hierarchy, drill-down page)
- ✅ Metadata Assignment (per-type per-field plugin priority config)
- ✅ Inbound sync — Trakt & SIMKL (watch history, ratings, watchlist, credits)
- ✅ FanEdit (IFDB) plugin — scrapes fanedit.org for fan edit metadata
- ✅ Hardcover plugin — book/audiobook metadata + reading history import
- ✅ Physical file vs metadata-only indicators
- ✅ Global search
- 🔲 Fanart.tv plugin
- 🔲 Plugin update notifications

### Phase 3: Advanced Features
- 🔲 Multi-user library sharing
- 🔲 Kodi/Plex scrobbler integration
- 🔲 Custom media types via UI
- 🔲 Docker support

### Phase 4: Ecosystem
- 🔲 Native mobile apps
- 🔲 Community plugin marketplace

---

## API Overview

```
POST /api/v1/auth/login               # Get JWT
POST /api/v1/auth/register
GET  /api/v1/media/search             # Search all media
GET  /api/v1/media/{id}               # Media detail + enrichment metadata
POST /api/v1/media/{id}/refresh/{pluginId}   # Trigger per-plugin enrichment
POST /api/v1/scrobble                 # Record a watch/listen event
POST /api/v1/sync/{pluginId}          # Trigger inbound sync
GET  /api/v1/scan/preview-grouped     # Hierarchical scan preview
POST /api/v1/scan/import-groups       # Import (202 + background)
GET  /api/v1/settings/metadata-assignment   # Metadata field assignments
GET  /api/v1/enrichment/stats         # Enrichment status summary
GET  /swagger                         # Interactive API docs
```

Auth: `Authorization: Bearer {jwt}` for web, `X-API-Key: chr_live_...` for scrobblers.

---

## Building a Plugin

Chronicle has a fully documented plugin system. See [docs/PLUGIN_AUTHORING.md](docs/PLUGIN_AUTHORING.md) for a complete guide covering:
- Project setup and `manifest.json` reference
- Implementing `IMetadataProvider` and `IImportProvider`
- Fix Match URL handling
- Settings schema and encryption
- Build/packaging and GitHub release publishing

The six existing plugins serve as reference implementations across different complexity levels — from a simple API-key metadata provider (TMDB) to a full OAuth import provider (Trakt) to an HTML-scraping provider (FanEdit).

---

## Credits

**Design, direction & testing:** Chronicle Contributors  
**Implementation:** Anthropic Claude (AI Assistant)  
**License:** MIT
