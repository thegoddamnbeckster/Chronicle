# Plugin Design Document: Wikipedia Hydrator (PLUGIN_WIKIPEDIA.md)

## Overview
The Wikipedia plugin for **Chronicle** acts as a **Secondary Text Hydrator**. While ingestion plugins (Trakt, Simkl, Hardcover) provide structured data and IDs, the Wikipedia plugin enriches the library with long-form encyclopedic summaries, production history, and biographical data that often exceed the character limits of standard media APIs.

## Objectives
- **Text Enrichment**: Fetch the "Lead Section" (intro) of Wikipedia articles for media and creators.
- **Biographical Data**: Provide birth/death/career summaries for Authors, Directors, and Actors.
- **Industrial Aesthetic**: Present text in a clean, plain-text format for the Chronicle UI.

## Data Ingestion & Metadata Mapping
Per `METADATA_ASSIGNMENT.md`, this plugin is a **Hydrator**. It does not create new media entries but "attaches" to existing ones once they have been identified by a primary source.

### 1. Search & Scoring Strategy
Since Wikipedia does not utilize IMDB or ISBN as primary keys, it uses Chronicle’s **Scoring Signal** routine to resolve matches.

| Signal | Weight | Logic |
| :--- | :--- | :--- |
| **Title Match** | +40 | Exact string match (normalized). |
| **Disambiguation Check** | +20 | Appending `(film)`, `(novel)`, or `(TV series)` to the query. |
| **Year Match** | +20 | Matches the year found in the first paragraph. |
| **Creator Match** | +15 | Matching the Author or Director name within the lead section. |

**Acceptance Threshold**: 60 (Higher threshold required to prevent "Common Name" collisions).

### 2. Hierarchy Support
The plugin resolves data based on the **HierarchyLevel** of the target:
- **Level 0 (Show/Book/Movie)**: Fetches the main article summary.
- **Level 1 (Season)**: Attempts to fetch "Season" specific articles (e.g., *Stranger Things (season 4)*); falls back to Level 0 if unavailable.
- **Level 2 (Episode)**: Generally Out of Scope for v1.0.

## Transfer Mechanisms (MediaWiki API)
- **Endpoint**: `https://en.wikipedia.org/w/api.php`
- **Mechanism**: 
    - `action=query`: To search and resolve page IDs.
    - `prop=extracts`: Using `exintro` and `explaintext` to get plain-text intro summaries without HTML bloat.
- **Format**: Data is requested as `format=json`.

## Methods of Storing Data
- **metadata_json**: The full plain-text extract is stored in the `metadata_json` blob under the key `chronicle.plugin.wikipedia`.
- **Identity Registry**: The `wikipedia_page_id` is stored in `external_ids` to ensure `GetByIdAsync` functions instantly on subsequent refreshes.
- **Local Cache**: All text is stored in the local SQLite database; no external calls are made during the rendering of the `DetailPanel`.

## Security & Privacy
In accordance with `SECURITY.md`:
- **Anonymous Access**: No user account or OAuth is required.
- **User-Agent**: The plugin identifies itself as `ChronicleMediaTracker/1.0` to comply with Wikimedia's API etiquette.
- **Zero-Tracking**: No local user watch history or library statistics are transmitted to Wikipedia.

## Logging & Observability
Strictly follows `LOGGING.md`:
- **ERR**: Network timeouts or MediaWiki API breaks.
- **WRN**: High-confidence match not found or "Disambiguation" page encountered.
- **INF**: "Enriched {MediaTitle} with {CharCount} characters of text."

## Plugin Catalog Specifications
- **ID**: `org.chronicle.wikipedia`
- **Category**: `Metadata / Hydrator`
- **Dependencies**: `core.metadata_manager`
