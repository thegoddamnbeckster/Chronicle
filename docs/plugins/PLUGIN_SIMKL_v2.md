# Chronicle.Plugin.Simkl v2

Metadata source plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) that fetches Movie, TV, and Anime metadata from [Simkl](https://simkl.com/).

**Plugin ID:** `chronicle.plugin.simkl`
**Version:** 2.0.0
**Media Types:** Movies (`movies`), TV Shows (`shows`), Seasons (`seasons`), Episodes (`episodes`), Anime (`anime`)
**Auth:** OAuth2 (Client ID & Secret)
**API:** Simkl API v2 — `https://api.simkl.com`

---

## Overview
V2 integrates Simkl's unique multi-category tracking (Anime/TV/Movies) into Chronicle's hierarchical model. It leverages the scoring logic and structural standards from the Hardcover v1 implementation to ensure high data integrity across diverse media types.

---

## Supported Media Types & Hierarchy
Simkl data is mapped into Chronicle's hierarchy, with special handling for Anime series.

| Media Type | HierarchyLevel | Parent Type | Description |
| :--- | :---: | :--- | :--- |
| `shows` | 0 | None | Top-level container for a TV series. |
| `anime` | 0 | None | Top-level container for an Anime series (Series/OVA/Movie). |
| `seasons` | 1 | `shows` or `anime` | Child container for specific season/arc blocks. |
| `episodes` | 2 | `seasons` | Individual media entries with watch events. |
| `movies` | 0 | None | Standalone cinematic items. |

---

## Search Strategy & Scoring
Following the Hardcover implementation, `SearchAsync` uses a weighted scoring system to resolve items when IDs are missing.

### ID Short-Circuit
If any of the following are present, bypass scoring and auto-accept:
- `IMDB_ID`
- `TMDB_ID`
- `MAL_ID` (MyAnimeList - Critical for Anime)

### Scoring Signals
| Signal | Score |
| :--- | :--- |
| Exact Title Match (Normalized) | +40 |
| Fuzzy Title Match (Levenshtein) | +20 |
| Year Exact Match | +20 |
| Category Match (e.g., Anime vs Movie) | +15 |

**Acceptance Threshold**: 50

---

## Data Storage (`metadata_json`)
All raw Simkl API responses are stored under the `chronicle.plugin.simkl` key in the item's `metadata_json` field.

```json
{
  "chronicle.plugin.simkl": {
    "simkl_id": 45678,
    "ids": { "simkl": 456, "mal": "12345", "imdb": "tt...", "tmdb": 789 },
    "status": "watching",
    "user_rating": 8,
    "total_episodes": 24
  }
}
```

---

## Security & Authentication
- **OAuth Vault**: All access and refresh tokens are stored in the encrypted `auth.vault`.
- **Credential Integrity**: Users must provide their own API credentials, ensuring no centralized logging of user activity.

## UI & Design Integration
- **Component Usage**: Implements `ActionBtn` for sync triggers and `ProgressBar` for ingestion feedback.
- **Media Labels**: Uses the `Tag` component to differentiate between "TV" and "Anime" in the library view.

## Logging & Observability
Strictly adheres to `LOGGING.md` standards:
- **ERR**: OAuth flow interruptions or API 500 errors.
- **WRN**: 429 Rate Limiting; implements Simkl-specific back-off headers.
- **INF**: Detailed ingestion metrics (e.g., "Updated progress for 12 Anime titles").
