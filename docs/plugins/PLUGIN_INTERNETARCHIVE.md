# Chronicle.Plugin.InternetArchive — Design Document

**Plugin ID:** `chronicle.plugin.internetarchive`
**Version:** 1.0.0
**Media Types:** Movies (`movie`), TV (`tv`), Music (`music`), Books (`book`), Software (`software`)
**Auth:** None required for read; optional account for uploads
**API:** Internet Archive Metadata API — `https://archive.org`

---

## Purpose

The [Internet Archive](https://archive.org/) is a non-profit digital library
preserving millions of movies, TV shows, recordings, books, software, and web
pages. Its open metadata API allows Chronicle to look up archive items by
identifier or search the catalogue. Particularly useful for public-domain
films, classic TV, and historical recordings not covered by commercial APIs.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `movie` | 6 | Public domain and classic films |
| `tv` | 6 | TV recordings |
| `music` | 6 | Live recordings, classic albums |
| `book` | 6 | Digitised books |
| `software` | 6 | Historical software |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Search | `GET https://archive.org/advancedsearch.php?q={query}&fl[]={fields}&output=json` |
| Item metadata | `GET https://archive.org/metadata/{identifier}` |
| Item files | `GET https://archive.org/metadata/{identifier}/files` |
| Download | `GET https://archive.org/download/{identifier}/{filename}` |
| Thumbnail | `https://archive.org/services/img/{identifier}` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `media_types_filter` | Media Collections | MultiSelect | No | `movies`, `audio`, `texts`, `software` |
| `search_limit` | Max Search Results | Number | No | Default: 10 |
| `prefer_public_domain` | Prefer Public Domain | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview, year, genres, creator, subject, description,
poster_url, runtime, archive_identifier, archive_url,
download_url, license, language
```

---

## Rate Limits

- No official rate limit; be polite (1 req/sec)
- The Advanced Search API can be slow — increase timeout to 15 s
- Thumbnail service may return 404 for items without cover images

---

## Implementation Notes

- The `identifier` field in IA responses is the canonical ID —
  store in `media_external_ids` with source `internet_archive`
- IA item types: `movies`, `audio`, `texts`, `software`, `image`,
  `collection`, `web`
- The metadata API returns a `files` array — look for `_thumb.jpg` or
  the first `.jpg` as the cover image
- For search, key fields to request: `identifier,title,creator,year,
  description,mediatype,runtime,subject`
- The `license` field indicates copyright status — items tagged
  `Public Domain` or Creative Commons are freely usable

---

## Scaffold Location

```
Chronicle.Plugin.InternetArchive/
├── Chronicle.Plugin.InternetArchive.csproj
├── README.md (this document)
├── manifest.json
├── InternetArchivePlugin.cs
└── Models/
    ├── ArchiveItem.cs
    ├── ArchiveSearchResult.cs
    └── ArchiveFile.cs
```
