# Chronicle.Plugin.Hardcover v2

Metadata source plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) that fetches book and audiobook metadata from [Hardcover](https://hardcover.app/).

**Plugin ID:** `chronicle.plugin.hardcover`
**Version:** 2.0.0
**Media Types:** Books (`books`), Audiobooks (`audiobooks`), Series (`book_series`)
**Auth:** Personal API token (hardcover.app/account/api)
**API:** Hardcover GraphQL API — `https://api.hardcover.app/v1/graphql`

---

## Overview
V2 introduces a hybrid hierarchical model [cite: 5]. This version adds support for `book_series` as a container while maintaining first-class support for standalone literature [cite: 5].

---

## Supported Media Types & Hierarchy
Chronicle v2 supports a flexible parent-child relationship to better organize vast libraries [cite: 5].

| Media Type | HierarchyLevel | Parent Type | Description |
| :--- | :---: | :--- | :--- |
| `book_series` | 0 | None | Container for a collection of books [cite: 5]. |
| `books` | 0 or 1 | `book_series` | Standalone items are Level 0; Series items are Level 1 [cite: 5]. |
| `audiobooks` | 0 or 1 | `book_series` | Standalone items are Level 0; Series items are Level 1 [cite: 5]. |

---

## Hierarchy Implementation Logic

### Standalone Mode (Inherited from v1)
* **Status**: Default for books with no series metadata [cite: 5].
* **Hierarchy**: Item remains at **HierarchyLevel 0** [cite: 5].
* **UI**: Rendered as a standard `MediaCard` at the root of the library [cite: 5].

### Series Mode (New in v2)
* **Trigger**: Detected via `book_series` metadata in the Hardcover GraphQL response [cite: 5].
* **Logic**:
    1. If a series is detected, a **HierarchyLevel 0** container (`book_series`) is verified or created [cite: 5].
    2. The individual book/audiobook is assigned as a **HierarchyLevel 1** child of that container [cite: 5].
    3. The `parent_id` field is populated with the ID of the series container [cite: 5].
* **Ordering**: Child items use the `position` field (supporting decimals like `1.5`) for chronological sorting within the container [cite: 5].

---

## UI Integration (Standardized)
Adhering to the `UI_DESIGN.md` component library:
* **Hybrid View**: The `DataGrid` renders Series as folders and Standalones as book covers [cite: 5].
* **Shelf Layout**: A vertical aspect-ratio layout optimized for book spines and covers [cite: 4].
* **Action Buttons**: Standardized `ActionBtn` for triggering library-wide syncs [cite: 4].

## Data Storage & Metadata
Per `METADATA_ASSIGNMENT.md`, v2 prioritizes specific identifiers [cite: 5]:
* **Global IDs**: ISBN-13, ISBN-10, and ASIN for deduplication across providers [cite: 5].
* **Metadata JSON**: Stores `moods`, `genres`, and `tags` for enhanced searchability [cite: 5].

## Security & Privacy
In accordance with `SECURITY.md`:
* **Zero-Trust**: API Tokens are stored in the encrypted `auth.vault` [cite: 2].
* **Local First**: All metadata and covers are cached locally to `/data/media/assets/` to ensure offline functionality [cite: 2].

## Logging
Per `LOGGING.md`:
* **Standardized Levels**: Uses `ERR`, `WRN`, and `INF` for tracking sync health and rate-limiting status [cite: 5].
