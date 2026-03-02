# Chronicle.Plugin.AllMusicClassical — Design Document

**Plugin ID:** `chronicle.plugin.allmusic.classical`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (web scraping — no public API)
**API:** AllMusic Classical — `https://www.allmusic.com` (classical section)

---

## Purpose

This plugin is a specialised variant of the AllMusic plugin, configured
specifically for **classical music**. AllMusic has a dedicated classical
section with composer-specific metadata, composition catalogs, period/form
classification, and expert editorial reviews written by classical music
scholars. It is operated as a separate plugin (rather than a setting on the
AllMusic plugin) because classical entries use a different URL structure,
different cast hierarchy (composer ≠ performer), and different metadata
fields.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 3 | Classical composition and recording metadata |
| `album` | 3 | Classical album with full performer credits |
| `artist` | 3 | Composer and performer profiles |

---

## Data Available (scraped)

| Page | Data Extracted |
|------|---------------|
| `/artist/{id}/{slug}` (composer) | Bio, born/died, nationality, style, influenced by/on |
| `/album/{id}/{slug}` | Title, composer, performers, label, year, genres, rating, review |
| `/composition/{id}/{slug}` | Work title, opus, catalogue number, genre, period |

AllMusic embeds schema.org `MusicAlbum` JSON-LD on album pages.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 2000 |
| `include_review` | Fetch Editorial Review | Boolean | No | Default: true |
| `include_moods` | Fetch Moods/Tones | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview (editorial review excerpt), year, genres,
cast (composer + conductor + orchestra + soloists), poster_url,
allmusic_classical_id, allmusic_url, rating,
metadata_json: { composer, conductor, ensemble, soloists,
                 opus_number, catalogue_ref, period, form,
                 styles, moods, tones, allmusic_rating,
                 allmusic_review_author }
```

---

## Rate Limits

- Identical to the base AllMusic plugin: minimum 2,000 ms between requests
- Cache all data for 14 days

---

## Implementation Notes

- This plugin should be registered with a **different plugin_id** from
  the base `chronicle.plugin.allmusic` — they target different URL
  patterns and extract different cast hierarchies
- Classical performer hierarchy in Chronicle cast:
  - `composer` → role `Composer`
  - `conductor` → role `Conductor`
  - ensemble/orchestra → role `Orchestra` / `Ensemble`
  - soloists → role based on instrument (e.g. `Piano`, `Violin`)
- AllMusic "Styles" for classical (e.g. `Romantic Era`, `Post-Romantic`,
  `Impressionist`) supplement the basic `genres` field
- AllMusic "Moods" (e.g. `Introspective`, `Majestic`, `Intimate`) are
  useful discovery metadata — store in `metadata_json.moods`
- Composition pages use `/composition/` URL path and are the canonical
  work-level pages (vs album-level pages for specific recordings)

---

## Scaffold Location

```
Chronicle.Plugin.AllMusicClassical/
├── Chronicle.Plugin.AllMusicClassical.csproj
├── README.md
├── manifest.json
├── AllMusicClassicalPlugin.cs
└── Models/
    └── AllMusicClassicalAlbum.cs
```
