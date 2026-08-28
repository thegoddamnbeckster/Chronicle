# Plugin Design Document: Fanart.tv Metadata Provider (PLUGIN_FANART.md)

## Overview
The Fanart.tv plugin for **Chronicle** serves as a **Metadata Enrichment Provider**. Unlike ingestion plugins (Trakt/Simkl) that focus on watch history, this plugin focuses on the high-quality visual presentation of the Chronicle library. It pulls high-resolution logos, clearart, disc art, and background images from the Fanart.tv API to enhance the "Industrial Aesthetic" of the UI.

## Security & Authentication
Following the protocols in `SECURITY.md`:
- **API Key Management**: Fanart.tv requires a personal API Key and a Client Key. These are stored in the encrypted `auth.vault`.
- **Read-Only Access**: This plugin never sends local user data to Fanart.tv. It only sends standardized IDs (IMDB/TMDB) to request image assets.
- **Traffic Localisation**: All fetched images are downloaded and cached locally to `/data/media/assets/` to ensure the UI remains functional offline and to protect user privacy from tracking pixels.

## UI Integration & Design
Adhering to the `UI_DESIGN.md` component library:
- **Asset Prioritization**: This plugin populates the `HeroHeader` and `MediaCard` components with "ClearLogo" and "HD ClearArt" to maintain the high-gloss, metal-hardware aesthetic of the Chronicle interface.
- **Visual Feedback**: A background worker status is visible in the "Settings > Metadata" panel using the `ProgressBar` to show hydration progress for the local library.
- **Fallback Logic**: If Fanart.tv lacks an asset, the UI defaults to the system-standard `PlaceholderSVG` as defined in the design system.

## Data Ingestion & Metadata Mapping
Per `METADATA_ASSIGNMENT.md`, this plugin acts as a **Hydrator**. It does not create media entries but enriches existing ones.

### 1. Asset Retrieval Logic
The plugin listens for `media.created` or `onTraktSyncComplete` events and triggers requests to the [Fanart.tv API](https://fanart.tv/api-docs/):
- **Movies**: `GET https://webservice.fanart.tv/v3/movies/{tmdb_id}`
- **TV Shows**: `GET https://webservice.fanart.tv/v3/tv/{tvdb_id}`

### 2. Supported Asset Mapping
| Fanart.tv Asset Type | Chronicle UI Mapping | Application |
| :--- | :--- | :--- |
| `hdmovielogo` / `hdtvlogo` | `assets.logo_clear` | Overlaid on industrial metal backgrounds |
| `moviebackground` | `assets.fanart_bg` | Full-width high-gloss backgrounds |
| `movedisc` / `cdart` | `assets.disc_render` | Used in the "Physical Media" view mode |
| `movierender` | `assets.character_art` | PNGs with transparency for UI depth |

## Logging & Observability
Implements the standardized `LOGGING.md` protocol:
- **Error Levels**:
    - `ERR`: API Key invalid (401) or API limit exceeded (429).
    - `WRN`: Asset not found (404) for a specific ID; plugin will skip to the next item.
    - `INF`: Log entries for "Asset Hydration: Downloaded 4 PNGs for {media_id}".
- **Log Location**: `/logs/plugins/fanart.log`.

## Plugin Catalog Specifications
- **ID**: `org.chronicle.fanart`
- **Category**: `Metadata/Hydrator`
- **Dependencies**: `core.metadata_manager`, `org.chronicle.trakt` (optional, for ID sourcing).
- **API Version**: Fanart.tv API v3.
