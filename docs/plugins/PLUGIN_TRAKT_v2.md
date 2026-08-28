# Chronicle.Plugin.Trakt v2

Metadata source plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) that fetches movie and TV metadata from [Trakt.tv](https://trakt.tv/).

**Plugin ID:** `chronicle.plugin.trakt`
**Version:** 2.0.0
**Media Types:** Movies (`movies`), TV Shows (`shows`), Seasons (`seasons`), Episodes (`episodes`)
**Auth:** OAuth2 (Client ID & Secret via user's Trakt API app)
**API:** Trakt API v2 — `https://api.trakt.tv`

---

## Overview
V2 moves away from a flat media structure to a multi-tiered hierarchical model [cite: 5]. It implements the high-fidelity scoring and storage patterns established in the Hardcover v1 codebase [cite: 5].

---

## Supported Media Types & Hierarchy
TV data is structured into a parent-child relationship, while movies remain independent [cite: 5].

| Media Type | HierarchyLevel | Parent Type | Description |
| :--- | :---: | :--- | :--- |
| `shows` | 0 | None | Top-level container for a series [cite: 5]. |
| `seasons` | 1 | `shows` | Child container for specific season blocks [cite: 5]. |
| `episodes` | 2 | `seasons` | Individual media entries with watch events [cite: 5]. |
| `movies` | 0 | None | Standalone items [cite: 5]. |

---

## Search Strategy & Scoring
Following the Hardcover implementation, `SearchAsync` uses a weighted scoring system [cite: 5].

### ID Short-Circuit
If `IMDB_ID` or `TMDB_ID` is present, bypass scoring and auto-accept [cite: 5].

### Scoring Signals
| Signal | Score |
| :--- | :--- |
| Exact Title Match (Normalized) | +40 [cite: 5] |
| Fuzzy Title Match (Levenshtein) | +20 [cite: 5] |
| Year Exact Match | +20 [cite: 5] |
| Year ±1 Match | +10 [cite: 5] |
| Creator/Director Match | +15 [cite: 5] |

**Acceptance Threshold**: 50 [cite: 5]

---

## Data Storage (`metadata_json`)
All raw Trakt responses are stored under the `chronicle.plugin.trakt` key in the item's `metadata_json` field before being mapped to Chronicle core fields [cite: 5].

```json
{
  "chronicle.plugin.trakt": {
    "trakt_id": 12345,
    "slug": "the-matrix-1999",
    "ids": { "trakt": 123, "imdb": "tt0133093", "tmdb": 603 },
    "rating": 9.1,
    "votes": 120000,
    "genres": ["action", "sci-fi"]
  }
}
```

---

## Security & Authentication
* **OAuth Vault**: Refresh and access tokens are stored in the encrypted `auth.vault` [cite: 2, 5].
* **Client Isolation**: Users provide their own Trakt API credentials to maintain local-first autonomy [cite: 2].

## UI & Design Integration
* **Component Usage**: Uses `ActionBtn` for sync triggers and `ProgressBar` for ingestion status [cite: 4].
* **Views**: TV Shows are rendered as folders (HierarchyLevel 0) with seasonal sub-folders [cite: 4, 5].

## Logging & Observability
Strictly adheres to `LOGGING.md` standards [cite: 5]:
* **ERR**: Authentication failure (401) or 5xx server errors [cite: 5].
* **WRN**: Rate limiting (429) back-off triggered [cite: 5].
* **INF**: Sync statistics (e.g., "Ingested 150 new episodes") [cite: 5].
