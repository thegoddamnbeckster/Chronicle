# Chronicle.Plugin.ClassicCat — Design Document

**Plugin ID:** `chronicle.plugin.classiccat`
**Version:** 1.0.0
**Media Types:** Music (`music`), Artists (`artist`)
**Auth:** None (fully public — no API key required)
**API:** ClassicCat — `https://www.classiccat.net`

---

## Purpose

[ClassicCat](https://www.classiccat.net/) is a free, community-curated
classical music catalogue database focused on **works and compositions**
rather than recordings. It provides a comprehensive index of classical
composers and their works with catalogue numbers (Op., BWV, K., etc.),
genre classifications, and instrumentation data. This plugin enriches
Chronicle's classical music entries with composition-level metadata —
the "what was composed" layer that complements the "who performed it"
data from Presto Music and AllMusic.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 4 | Composition metadata (opus, instrumentation) |
| `artist` | 3 | Composer profile and works catalogue |

---

## Data Available (scraped)

| Page | Data Extracted |
|------|---------------|
| `/composer/{slug}/` | Composer bio, birth/death, nationality, works list |
| `/composer/{slug}/{work_slug}/` | Work detail: title, catalogue ref, genre, instrumentation, dates |
| `/search/?q=` | Search across composers and works |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 1500 |

---

## Fields Populated

```
title, overview (work description), year (composed), cast (composer),
classiccat_work_id, classiccat_url,
metadata_json: { catalogue_number, opus, key_signature,
                 instrumentation, form, period, nationality,
                 composed_date, premiere_date }
```

---

## Rate Limits

- Small community site — be respectful; minimum 1,500 ms between requests
- Cache all data for 30 days — composition data is extremely stable

---

## Implementation Notes

- ClassicCat is a **composition-level** source, not a recordings source
  — it describes the musical work, not a specific performance
- Use this to enrich the `media_item` representing a classical work
  (the "parent" in Chronicle's hierarchy) with composition metadata
- `catalogue_number` formats vary by composer:
  BWV (Bach), K./KV (Mozart), D. (Schubert), Op. (most composers)
  — store raw and also parse into `opus` field
- `instrumentation` (e.g. "String Quartet", "Piano Solo", "Orchestra")
  is useful for filtering and discovery
- `form` (Symphony, Sonata, Concerto, Prelude…) maps to Chronicle's genre
  or a dedicated classical `sub_genre` field in metadata
- This plugin has low priority (4) as its data supplements rather than
  replaces performer-centric sources

---

## Scaffold Location

```
Chronicle.Plugin.ClassicCat/
├── Chronicle.Plugin.ClassicCat.csproj
├── README.md
├── manifest.json
├── ClassicCatPlugin.cs
└── Models/
    ├── ClassicCatComposer.cs
    └── ClassicCatWork.cs
```
