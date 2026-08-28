# Chronicle.Plugin.Fanart v2

Metadata enrichment plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) that fetches high-quality visual assets from [Fanart.tv](https://fanart.tv/).

**Plugin ID:** `chronicle.plugin.fanart`
**Version:** 2.0.0
**Asset Types:** ClearArt, ClearLogos, Backgrounds, DiscArt
**Hierarchy Support:** Show-level, Season-level, and Episode-level assets [cite: 5]
**API:** Fanart.tv API v3 — `https://webservice.fanart.tv/v3/`

---

## Overview
V2 integrates with Chronicle's hierarchical model to provide "Scoped Asset Resolution." This ensures that a TV Season uses season-specific art when available, but gracefully falls back to Show-level assets [cite: 5].

---

## Hierarchical Asset Resolution
Following the **HierarchyLevel** logic, Fanart v2 fetches and applies assets based on the item's depth [cite: 5].

| Entity Depth | Asset Scope | Fallback Logic |
| :--- | :--- | :--- |
| **HierarchyLevel 0 (Show/Series)** | Global Logos & Backgrounds | Uses `hdtvlogo`, `showbackground` |
| **HierarchyLevel 1 (Season)** | Season-specific Banners/Thumbnails | Falls back to Level 0 Backgrounds if null |
| **HierarchyLevel 2 (Episode)** | Production Stills | Falls back to Level 1 Season Art |

---

## Scored Search Integration
Per the **Scoring Signal** standards, this plugin uses IDs sourced by Ingestion Plugins (Trakt/Simkl) to avoid fuzzy-matching visually critical assets [cite: 5].

1. **Short-Circuit**: Uses `TMDB_ID` or `TVDB_ID` from the `metadata_json` blob [cite: 5].
2. **Assignment**: Assets are mapped to the `assets` object in the Chronicle database and cached locally [cite: 2].

---

## Technical Storage
- **metadata_json**: Stores the Fanart-specific image IDs and dimensions to prevent redundant downloads [cite: 5].
- **Local Cache**: Assets are stored in `/data/media/assets/{media_id}/` following the system-wide privacy and offline-first policy [cite: 2].

## UI & Design Integration
- **Industrial Aesthetic**: Prioritizes `hdmovielogo` (ClearLogos) for the `HeroHeader` to ensure text readability over high-gloss backgrounds [cite: 4].
- **Component Feedback**: Uses `ProgressBar` during "Library Hydration" tasks [cite: 4].

## Logging & Observability
Strictly adheres to `LOGGING.md`:
- **ERR**: API Key invalid (401) or Client Key missing [cite: 5].
- **WRN**: No assets found for a high-confidence ID (404) [cite: 5].
- **INF**: "Hydration complete for {media_name}: 4 assets downloaded" [cite: 5].
