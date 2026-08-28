# Plugin Design Document: Trakt.tv Integration (PLUGIN_TRAKT.md)

## Overview
The Trakt plugin for **Chronicle** provides a secure, high-fidelity bridge to the Trakt.tv API. It is designed to function as a **Primary Ingestion Provider**, feeding the local Chronicle database with historical and real-time media events while adhering to the system's strict privacy, metadata, and security standards.

## Security & Authentication
In accordance with `SECURITY.md`, the Trakt plugin operates on a "Zero-Trust" local model.
- **Credential Isolation**: OAuth2 `access_token` and `refresh_token` are stored in the encrypted `auth.vault` within the plugin's private directory. They are never exposed to the UI or logged.
- **Scoped Permissions**: The plugin requests `public` and `history` scopes only. It does not request write access to a user’s Trakt account unless "Scrobbling" is explicitly enabled in settings.
- **Client Ownership**: To prevent centralized tracking, users are encouraged to provide their own Trakt API Client ID/Secret.

## UI Integration & Design
Following the `UI_DESIGN.md` component library, the Trakt plugin contributes to the following views:
- **Settings Dashboard**: A dedicated card within the "Source Manager" featuring a connection status indicator (Active/Expired) and a "Sync Now" button utilizing the standard `ActionBtn` component.
- **Onboarding Flow**: A multi-step modal that guides the user through the Trakt OAuth handshake.
- **Activity Feedback**: Uses the `ProgressBar` and `Toast` notifications to inform the user of sync progress and completion.

## Data Ingestion & Metadata Mapping
Per `METADATA_ASSIGNMENT.md`, the Trakt plugin acts as a **Source of Truth** for watch history, but a **Secondary Source** for media details.

### 1. Unique Identification
To ensure no duplication across multiple sources (e.g., Plex and Trakt), the plugin follows the Chronicle hierarchy:
1.  **Match by IMDB ID** (Highest priority)
2.  **Match by TMDB ID**
3.  **Create Placeholder**: If no ID exists, create a "Shell Entry" and flag it for the `METADATA_HYDRATOR` plugin to resolve.

### 2. Assignment Logic
When a watch event is imported:
- **Timestamp**: Extracted from Trakt's `watched_at` field.
- **Provenance**: The `source_origin` attribute is set to `trakt`.
- **Identity Registry**: Trakt-specific IDs are stored in the `external_ids` mapping, allowing other plugins in the `PLUGIN_CATALOGUE` to link records.

## Logging & Observability
The plugin implements the standardized `LOGGING.md` protocol:
- **Error Levels**: 
    - `ERR`: OAuth token expiration or 401 Unauthorized.
    - `WRN`: Rate limiting (429) encountered.
    - `INF`: Sync cycle stats.
- **Log Location**: `/logs/plugins/trakt.log` following the system-wide rotation policy.

## Plugin Catalog Specifications
- **ID**: `org.chronicle.trakt`
- **Category**: `Ingestion`
- **Dependencies**: `core.metadata_manager`
- **Conflicts**: None.
