# Chronicle.Plugin.TiltfactorGames — Design Document

**Plugin ID:** `chronicle.plugin.tiltfactorgames`
**Version:** 1.0.0
**Media Types:** Games (`game`)
**Auth:** None (public)
**API:** Tiltfactor Metadata Games — `https://metadatagames.org`

---

## Purpose

[Tiltfactor's Metadata Games](https://metadatagames.org/) is an academic
project from the Tiltfactor Game Research Lab at Dartmouth College. It uses
crowd-sourced metadata games (human computation) to generate and verify
metadata tags for cultural heritage objects — including games, images, and
archival media. This plugin provides access to community-generated descriptive
metadata for items in the Metadata Games collections.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `game` | 9 | Community-tagged game and cultural heritage metadata |

---

## API Overview

Metadata Games provides a REST-ish API for accessing collection items
and their community-generated tags.

| Operation | Endpoint |
|-----------|---------|
| Collection list | `GET /api/collections` |
| Items in collection | `GET /api/collections/{id}/items` |
| Item detail | `GET /api/items/{id}` |
| Item tags | `GET /api/items/{id}/tags` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `collections` | Collection Filter | MultiSelect | No | Filter to specific collections |
| `min_tag_count` | Min Tag Count | Number | No | Default: 3 (minimum crowd consensus) |

---

## Fields Populated

```
title, genres (community tags), overview (description),
metadata_json: { community_tags, tag_counts, collection_name,
                 tiltfactor_item_id }
```

---

## Rate Limits

- Academic project; be polite (1 req/sec, cache results)

---

## Implementation Notes

- This is a supplementary/experimental source; use very low priority (9)
- Community tags provide folksonomy-style genre/description tagging that
  complements structured databases
- The project's primary use case is digital humanities research; it may
  have limited coverage of mainstream commercial games
- Verify current API availability at metadatagames.org before implementing
  — the project's operational status should be confirmed

---

## Scaffold Location

```
Chronicle.Plugin.TiltfactorGames/
├── Chronicle.Plugin.TiltfactorGames.csproj
├── README.md (this document)
├── manifest.json
├── TiltfactorGamesPlugin.cs
└── Models/
    └── MetadataGamesItem.cs
```
