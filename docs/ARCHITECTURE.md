# Chronicle Architecture

**Version:** 1.0  
**Last Updated:** 2026-01-12  
**Author:** Michael Beck with Anthropic Claude

---

## System Overview

Chronicle is built as a self-hosted web application using modern, cross-platform technologies. The architecture prioritizes extensibility, maintainability, and user control.

### Core Principles

1. **Plugin-First Design** - Core functionality through plugins, not hardcoded
2. **Configuration Over Code** - UI and behavior driven by database config
3. **Privacy & Ownership** - User data stays on user's server
4. **Safe Operations** - Automatic backups, rollback capability, safe mode
5. **Cross-Platform** - Windows, Linux, macOS support
6. **Lossless Ingestion** - Everything received is persisted; nothing silently discarded. Fields that don't map to first-class schema columns go into `metadata_json`, partitioned by source (e.g. `{"tmdb": {...}, "fileScanner": {...}}`). Data is never lost at the point of ingestion and can be re-processed later without a re-fetch.

---

## Technology Stack

### Backend
- **.NET Core 8.0** (C#)
- **ASP.NET Core** - Web framework
- **Entity Framework Core** - ORM
- **Kestrel** - Built-in web server (no IIS/Apache needed)

### Frontend
- **React 18** with TypeScript
- **Responsive design** (mobile-friendly)
- **Matches Sonarr/Radarr aesthetic**

### Database
- **SQLite** (default) - Zero configuration, single file
- **PostgreSQL** (production option) - Better performance at scale

### Platform Support
- Windows 10/11 (native .exe)
- Linux (all major distributions)
- macOS (Intel & Apple Silicon)
- Docker (all platforms)

---

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   Web Browser (React)                   │
└────────────────────┬────────────────────────────────────┘
                     │ HTTPS/HTTP
┌────────────────────▼────────────────────────────────────┐
│            Kestrel Web Server (ASP.NET Core)            │
├─────────────────────────────────────────────────────────┤
│                  REST API Layer                         │
│  /api/v1/scrobble  /api/v1/media  /api/v1/users       │
├─────────────────────────────────────────────────────────┤
│                 Business Logic Layer                    │
│  • MediaService  • UserService  • ScrobbleProcessor    │
│  • StatsEngine  • PluginManager  • BackgroundJobs     │
├─────────────────────────────────────────────────────────┤
│                   Plugin System                         │
│  • Metadata Scrapers  • Media Types  • Widgets         │
├─────────────────────────────────────────────────────────┤
│              Data Access Layer (EF Core)                │
├─────────────────────────────────────────────────────────┤
│         Database (SQLite / PostgreSQL)                  │
└─────────────────────────────────────────────────────────┘

External:
┌──────────────┐         ┌──────────────┐
│  Scrobblers  │────────►│  Chronicle   │
│ (Kodi, Plex) │         │  Updater     │
└──────────────┘         └──────────────┘
```

---

## Directory Structure

```
/chronicle/
├── /bin/
│   ├── /current/           → symlink to active version
│   ├── /v1.0.0/           
│   ├── /v1.1.0/
│   └── /v1.2.0/
├── /data/
│   ├── chronicle.db        (SQLite database)
│   └── config.json
├── /logs/
│   ├── chronicle.log
│   ├── chronicle.log.1
│   └── /plugins/
│       ├── tmdb-scraper.log
│       └── imdb-scraper.log
├── /backups/
│   ├── pre-v1.1.0-upgrade.db
│   └── pre-v1.2.0-upgrade.db
├── /plugins/
│   ├── /active/
│   │   ├── tmdb-scraper.dll
│   │   └── musicbrainz-scraper.dll
│   ├── /backups/
│   └── /configs/
│       ├── tmdb-scraper.json
│       └── musicbrainz-scraper.json
└── updater.exe             (separate process)
```

---

## Key Components

### 1. API Layer
- RESTful endpoints
- JWT authentication
- Swagger documentation at `/swagger`
- Versioned (`/api/v1/`, `/api/v2/`)

### 2. Plugin System
- **IMetadataProvider** - Scraper plugins
- **IMediaTypePlugin** - Custom media types
- **IWidgetPlugin** - Dashboard widgets
- Hot-reload capable (disable/enable without restart)

### 3. Background Job System
- Built-in scheduler (no cron/Task Scheduler needed)
- Jobs: File verification, metadata refresh, stats cache, cleanup, backups
- Configurable schedules (cron expressions)
- Job history tracking

### 4. Update System
- Separate updater process
- Checks GitHub Releases API
- Downloads, verifies checksums
- Atomic version switching (symlinks)
- Automatic rollback on failure
- Health checks post-update

### 5. Logging System
- Log4j-style rolling file appenders
- Size-based rollover (25MB max)
- Per-component log levels
- Thread-safe, multi-threaded
- Separate logs per plugin

---

## Data Flow Examples

### Scrobble Flow

```
1. Kodi plays episode
   ↓
2. Kodi addon sends POST /api/v1/scrobble
   ↓
3. API validates token, extracts media info
   ↓
4. ScrobbleProcessor identifies media item
   ↓
5. Save to interaction_events table
   ↓
6. Update user_libraries status
   ↓  
7. Update currently_watching table
   ↓
8. Trigger webhooks (if configured)
   ↓
9. Return 200 OK to Kodi
```

### Metadata Fetching Flow

```
1. User adds new movie
   ↓
2. MediaService.FetchMetadataAsync(query)
   ↓
3. Get ordered list of scrapers for "movie" type
   ↓
4. Try TMDB scraper (priority 1)
   ↓
5. TMDB returns results
   ↓
6. Save metadata + external IDs to database
   ↓
7. Download poster/backdrop (store URLs only)
   ↓
8. Return to user
```

---

## Scalability Considerations

### Single User
- SQLite adequate
- ~100MB database per year of tracking
- No optimization needed

### Family (2-5 users)
- SQLite still fine
- Consider PostgreSQL if >10k scrobbles/month

### Power Users / Groups (10+ users)
- PostgreSQL recommended
- Partition `interaction_events` by date
- Enable full-text search indexes
- Consider read replicas (future)

---

## Security Architecture

### Authentication
- Bcrypt password hashing (cost 12)
- JWT tokens for web sessions
- API keys for scrobblers
- Optional 2FA (future)

### Authorization
- Role-based access control (Admin, User, Limited)
- Per-resource permissions
- API rate limiting per token/IP

### Encryption
- Passwords: Bcrypt (never plaintext)
- API keys/secrets: AES-256 encryption
- Optional: Database encryption at rest
- HTTPS via reverse proxy (Nginx, Caddy)

---

## Performance Targets

| Metric | Target | Notes |
|--------|--------|-------|
| API Response Time | <100ms (p95) | For standard queries |
| Database Query Time | <50ms (p95) | With proper indexing |
| UI Load Time | <2s | First contentful paint |
| Scrobble Processing | <200ms | End-to-end |
| Concurrent Users | 100+ | On modest hardware |

---

## Monitoring & Health

### Health Check Endpoint
`GET /api/health`

```json
{
  "status": "healthy",
  "version": "1.2.0",
  "uptime_seconds": 3600,
  "database": "connected",
  "plugins": {
    "loaded": 5,
    "failed": 0
  },
  "disk_space_mb": 15000
}
```

### Metrics (Future)
- Prometheus endpoint
- Grafana dashboards
- Alert rules

---

## Disaster Recovery

### Backup Strategy
1. **Automatic** - Daily at 1am, before updates
2. **Manual** - User-triggered via UI
3. **Retention** - Last 30 days, or custom

### Recovery Procedures
1. **Database corruption** - Restore from backup
2. **Bad update** - Automatic rollback or manual version select
3. **Plugin failure** - Safe mode disables all plugins
4. **Configuration error** - Reset to defaults option

---

## Future Architecture Considerations

### Phase 2+
- API versioning strategy (v2, v3)
- GraphQL endpoint (alternative to REST)
- WebSocket support (real-time updates)
- Message queue (RabbitMQ/Redis) for async processing

### Phase 3+
- Microservices (if needed at scale)
- Distributed caching (Redis)
- CDN for static assets
- Federation protocol (instance-to-instance)

### Phase 4+
- Mobile apps (native iOS/Android)
- Browser extension
- Voice assistant integration (Alexa, Google Home)
- Machine learning recommendations

---

## Development Practices

### Code Organization
```
/src/
├── Chronicle.Core/          (Domain models, interfaces)
├── Chronicle.Data/          (Database, repositories)
├── Chronicle.Services/      (Business logic)
├── Chronicle.API/           (REST API controllers)
├── Chronicle.Plugins/       (Plugin interfaces, manager)
├── Chronicle.Web/           (React frontend)
└── Chronicle.Tests/         (Unit & integration tests)
```

### Testing Strategy
- Unit tests: 80%+ coverage
- Integration tests: Critical paths
- E2E tests: User flows
- Performance tests: Load testing

### CI/CD Pipeline
1. Push to GitHub
2. GitHub Actions runs tests
3. Build binaries (Windows, Linux, macOS)
4. Run security scans
5. Create release artifacts
6. Publish Docker image

---

**Document Status:** Design Phase  
**Next Review:** Implementation Start
