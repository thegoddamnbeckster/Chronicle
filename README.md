# Chronicle

**Universal Media Tracking Platform**

Chronicle is a self-hosted, open-source media tracking application that allows you to track any type of media consumption - movies, TV shows, music, books, anime, podcasts, and more. Built with privacy, extensibility, and user control as core principles.

---

## Features

- 📊 **Universal Media Tracking** - Track any media type through configurable plugins
- 🔄 **Rewatch Cycles** - Track multiple viewing sessions of the same content
- 📦 **Version Management** - Handle different cuts, editions, and fan edits
- 👥 **Multi-User & Family Groups** - Share tracking with family or friends
- 🔌 **Extensible Plugin System** - Add new metadata sources and media types
- 🎨 **Customizable Dashboard** - Build your own home page with widgets
- 🔒 **Privacy First** - Your data stays on your server
- 🔄 **Safe Updates** - Automatic updates with rollback capability
- 📱 **Scrobbler Support** - Kodi, Plex, MPC-HC, and more

---

## Quick Start

### Docker (Recommended)

```bash
docker run -d \
  --name chronicle \
  -p 8080:8080 \
  -v /path/to/data:/data \
  chronicle/chronicle:latest
```

### Windows

1. Download `Chronicle-v1.0.0-windows.zip`
2. Extract to desired location
3. Run `Chronicle.exe`
4. Open browser to `http://localhost:8080`

### Linux

```bash
wget https://github.com/chronicle/chronicle/releases/download/v1.0.0/chronicle-linux-x64.tar.gz
tar -xzf chronicle-linux-x64.tar.gz
cd chronicle
./Chronicle
```

---

## Documentation

- [Architecture Overview](ARCHITECTURE.md) - System design and technology stack
- [Features](FEATURES.md) - Detailed feature specifications
- [Database Schema](DATABASE_SCHEMA.md) - Complete database design
- [API Specification](API_SPECIFICATION.md) - REST API documentation
- [Plugin Development](PLUGIN_SYSTEM.md) - Creating plugins
- [Deployment Guide](DEPLOYMENT.md) - Installation and updates
- [Security](SECURITY.md) - Authentication and encryption
- [UI Design](UI_DESIGN.md) - Interface customization
- [Logging](LOGGING.md) - Logging system details
- [Development Guide](DEVELOPMENT.md) - Contributing to Chronicle

---

## Technology Stack

- **Backend:** .NET Core 8.0 (C#)
- **Frontend:** React with TypeScript
- **Database:** SQLite (default) / PostgreSQL (production)
- **Web Server:** Kestrel (built-in)
- **Platform:** Cross-platform (Windows, Linux, macOS)

---

## Comparison to Existing Solutions

| Feature | Chronicle | Trakt | SIMKL | Plex |
|---------|-----------|-------|-------|------|
| Self-hosted | ✅ | ❌ | ❌ | ✅ |
| Open source | ✅ | ❌ | ❌ | ❌ |
| Universal media | ✅ | ❌ | ✅ | ❌ |
| Rewatch tracking | ✅ | ❌ | ❌ | ⚠️ |
| Version management | ✅ | ❌ | ❌ | ❌ |
| Custom media types | ✅ | ❌ | ❌ | ❌ |
| Plugin scrapers | ✅ | ❌ | ❌ | ⚠️ |
| Data ownership | ✅ | ❌ | ❌ | ✅ |

---

## Project Status

**Current Phase:** Planning & Design  
**Target v1.0:** Q2 2026

### Roadmap

**Phase 1: MVP (v0.1 - v0.5)**
- [ ] Core API (scrobble, history, users)
- [ ] Basic web UI
- [ ] SQLite database
- [ ] Single media type (TV shows)
- [ ] Windows executable

**Phase 2: Core Features (v0.6 - v1.0)**
- [ ] Multiple media types (TV, movies, music)
- [ ] Plugin system for scrapers
- [ ] Version management
- [ ] Rewatch sessions
- [ ] Kodi scrobbler
- [ ] Docker support

**Phase 3: Advanced Features (v1.1 - v2.0)**
- [ ] Multi-user support
- [ ] Family units/groups
- [ ] Custom media types
- [ ] Import from Trakt/SIMKL
- [ ] Mobile-responsive UI

**Phase 4: Ecosystem (v2.1+)**
- [ ] Native mobile apps
- [ ] Advanced analytics
- [ ] Federation (connect instances)
- [ ] Community plugin marketplace

---

## Contributing

We welcome contributions! Please see [DEVELOPMENT.md](DEVELOPMENT.md) for:
- Code of conduct
- Development workflow
- Coding standards
- Testing guidelines
- Pull request process

---

## Credits

**Design & Testing:** Michael Beck  
**Implementation:** Anthropic Claude (AI Assistant)  
**License:** MIT

This project was designed by Michael Beck with implementation assistance from Anthropic's Claude AI. All architectural decisions, feature requirements, and testing are driven by human direction.

---

## License

MIT License - see [LICENSE](LICENSE) file for details

---

## Support

- **Documentation:** [docs.chronicle.app](https://docs.chronicle.app) *(future)*
- **Issues:** [GitHub Issues](https://github.com/chronicle/chronicle/issues)
- **Discussions:** [GitHub Discussions](https://github.com/chronicle/chronicle/discussions)
- **Discord:** [discord.gg/chronicle](https://discord.gg/chronicle) *(future)*

---

## Acknowledgments

Chronicle is inspired by and designed to work alongside:
- Sonarr/Radarr/Lidarr (*arr stack)
- Trakt.tv
- SIMKL
- Kodi
- Plex/Jellyfin/Emby

Special thanks to the open source community for building the tools and infrastructure that make projects like this possible.

---

**Chronicle** - Your media, your data, your way.
