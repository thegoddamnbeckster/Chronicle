# Chronicle.Plugin.VideoGameResources — Design Document

**Plugin ID:** `chronicle.plugin.videogameresources`
**Version:** 1.0.0
**Media Types:** Games (`game`)
**Auth:** None (GitHub raw content)
**API:** amiaopensource/video-game-resources — GitHub repository

---

## Purpose

The [amiaopensource/video-game-resources](https://github.com/amiaopensource/video-game-resources)
repository is a curated open list of video game preservation resources,
tools, and databases maintained by the Association of Moving Image Archivists
(AMIA). This Chronicle plugin provides access to the structured resource
data in this repository, enabling Chronicle to cross-reference games with
known preservation records, archive locations, and documentation resources.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `game` | 10 | Game preservation cross-reference only |

---

## API Overview

This is a static GitHub repository, not a live API. The plugin fetches
structured data files (JSON, YAML, CSV) from the repository via
GitHub's raw content CDN.

| Operation | URL |
|-----------|-----|
| Fetch resource list | `GET https://raw.githubusercontent.com/amiaopensource/video-game-resources/main/resources.json` |
| Browse repository | GitHub API: `GET https://api.github.com/repos/amiaopensource/video-game-resources/contents/` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `github_token` | GitHub Token | Password | No | Increases rate limit from 60 to 5000 req/hr |
| `refresh_days` | Cache Duration (days) | Number | No | Default: 7 |

---

## Fields Populated

```
metadata_json: { preservation_resources: [{ name, url, type, notes }],
                 archive_status, emulation_support }
```

> This plugin adds supplementary preservation metadata to existing game
> records — it does not populate core `MediaMetadata` fields.

---

## Rate Limits

- GitHub API: 60 req/hr unauthenticated; 5,000/hr with token
- Raw content CDN: no published limit; cache aggressively

---

## Implementation Notes

- This is a supplementary enrichment plugin; assign lowest priority (10)
- The repository structure may change; implement a flexible parser that
  reads the README to discover available data files
- Primarily useful for game archivists and preservationists tracking
  which games have documentation and emulation support
- AMIA's interest is in moving image content — the video game resources
  here focus on games-as-cultural-artifacts
- Consider merging this with the InternetArchive plugin for a unified
  "preservation and archival" plugin category

---

## Scaffold Location

```
Chronicle.Plugin.VideoGameResources/
├── Chronicle.Plugin.VideoGameResources.csproj
├── README.md (this document)
├── manifest.json
├── VideoGameResourcesPlugin.cs
└── Models/
    └── PreservationResource.cs
```
