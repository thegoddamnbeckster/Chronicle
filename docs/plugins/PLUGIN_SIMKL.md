# Plugin Design Document: Simkl Integration (PLUGIN_SIMKL.md)

## Overview
The Simkl plugin for **Chronicle** provides a secure, high-fidelity integration with the Simkl.com API. This plugin functions as an **Ingestion Provider**, specifically targeting the "all-in-one" media tracking capabilities of Simkl (Anime, TV, and Movies). It adheres to Chronicle's local-first privacy, metadata, and security standards.

## Security & Authentication
Following the protocols in `SECURITY.md`:
- **Authentication**: Utilizes the [Simkl OAuth2 API](https://simkl.docs.apiary.io/#reference/authentication).
- **Credential Isolation**: The `access_token` and `client_id` are stored within the encrypted `auth.vault` in the plugin’s private directory.
- **Privacy**: No local user data is sent back to Simkl. The plugin only requests read-access scopes (`/sync/all-items`) unless the "Scrobble to Simkl" feature is manually enabled.

## UI Integration & Design
Adhering to the `UI_DESIGN.md` component library:
- **Settings Dashboard**: A Simkl-branded integration card in the "Source Manager" featuring a "Last Synced" timestamp and a "Sync All" button using the `ActionBtn` component.
- **Importer Interface**: An optional list-view using `DataGrid` to allow users to select specific Simkl categories (Anime, TV, or Movies) to include or exclude from the local database.
- **Visual Feedback**: Sync status is displayed via a standard `ProgressBar` with `Toast` notifications for successful credential refreshes.

## Data Ingestion & Metadata Mapping
Per `METADATA_ASSIGNMENT.md`, the Simkl plugin acts as a primary source for specific media types, particularly Anime.

### 1. Unique Identification & Cross-Referencing
Simkl provides robust cross-platform IDs which are utilized to ensure zero duplication:
1.  **Match by IMDB ID**: Primary for Movies/TV.
2.  **Match by TMDB ID**: Secondary for Movies/TV.
3.  **Match by MAL (MyAnimeList) ID**: Primary for Anime content.
4.  **Simkl ID**: Stored as a fallback in the `external_ids` mapping for specific metadata hydration via Simkl APIs.

### 2. API Transfer Mechanisms
- **Endpoint**: Uses `GET https://api.simkl.com/sync/all-items/` to retrieve the user's entire library.
- **Incremental Updates**: Uses the `GET https://api.simkl.com/sync/activities` endpoint to check for changes since the last local timestamp to minimize bandwidth.
- **Media Mapping**:
    - `last_watched_at` -> `history.timestamp`
    - `user_rating` -> `media.rating`
    - `status` (watching, plan to watch, completed) -> `media.status`

## Logging & Observability
Implements the standardized `LOGGING.md` protocol:
- **Error Levels**:
    - `ERR`: Simkl API 500/503 (Server side) or 401 (Invalid Token).
    - `WRN`: 403 (Rate limit exceeded); implements exponential back-off.
    - `INF`: Detailed sync reports (e.g., "Identified 120 Anime titles, 45 Movies").
- **Log Location**: `/logs/plugins/simkl.log` with a 10MB rotation limit.

## Plugin Catalog Specifications
- **ID**: `org.chronicle.simkl`
- **Category**: `Ingestion`
- **Dependencies**: `core.metadata_manager`
- **API Version**: Simkl API v2
- **Conflicts**: None.
