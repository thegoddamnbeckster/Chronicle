# Chronicle Plugin Specification: SIMKL (V3)

This document provides a complete implementation specification for the SIMKL metadata plugin for Chronicle. It is designed to allow a developer to write a .NET 9 class library without further API research.

---

## Section 1 — Service Overview
**Service Name:** SIMKL  
**Website:** [https://simkl.com](https://simkl.com)

SIMKL is a comprehensive tracking service for movies, TV shows, and anime. It integrates metadata from multiple sources (IMDB, TMDB, MAL) and provides a unified API for media discovery and metadata.

**Chronicle Media Types Supported:**
- `movie` (Hierarchy Level 0)
- `tv` (Hierarchy Level 0)
- `anime` (Hierarchy Level 0)
- `season` (Hierarchy Level 1)
- `episode` (Hierarchy Level 2)

**Availability Constraint:** This plugin is strictly limited to **Movies**, **TV**, and **Anime**. It should only be registered as a metadata provider for these specific first-class types.

---

## Section 2 — Authentication & Credential Acquisition
**Mechanism:** Header-based Authentication.
**Required Headers:**
- `Content-Type: application/json`
- `simkl-api-key: {CLIENT_ID}`

**Step-by-Step Acquisition:**
1. Login to [SIMKL](https://simkl.com).
2. Navigate to [Settings > API Applications](https://simkl.com/settings/developer/).
3. Create a new application.
4. Copy the **Client ID**.

---

## Section 3 — Plugin Settings Schema
| Key | Label | Description | Type | Required | Default |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `client_id` | SIMKL Client ID | Your SIMKL API Client ID from the developer portal. | Password | True | null |

---

## Section 4 — Manifest Values
**`manifest.json`**
```json
{
  "plugin_id": "chronicle.plugin.simkl",
  "name": "SIMKL",
  "version": "1.0.0",
  "author": "Chronicle Contributors",
  "description": "Comprehensive metadata for Movies, TV, and Anime from SIMKL.com.",
  "min_chronicle_version": "1.0.0",
  "entry_type": "Chronicle.Plugin.Simkl.SimklMetadataProvider",
  "iconUrl": "https://simkl.com/favicon.ico",
  "brandColorLight": "#000000",
  "brandColorDark": "#FFFFFF",
  "fixMatchHint": "Enter SIMKL ID, IMDB ID (tt...), or SIMKL URL.",
  "supported_media_types": ["movie", "tv", "anime"],
  "background_tasks": []
}
```

---

## Section 5 — Search Endpoint Specification
### Endpoint: Text Query Search
SIMKL uses specific endpoints per media type.
- **Movie:** `GET https://api.simkl.com/search/movie?q={query}`
- **TV:** `GET https://api.simkl.com/search/tv?q={query}`
- **Anime:** `GET https://api.simkl.com/search/anime?q={query}`

**Example Request (Movie):**
`GET https://api.simkl.com/search/movie?q=the+batman`

**Example Response:**
```json
[
  {
    "title": "The Batman",
    "year": 2022,
    "ids": {
      "simkl": 636830,
      "slug": "the-batman",
      "imdb": "tt1877830",
      "tmdb": "414906"
    },
    "poster": "63/636830_0"
  }
]
```

---

## Section 6 — Fetch-by-ID Endpoint Specification
To get full metadata, append `?extended=full`.
- **Movie:** `GET https://api.simkl.com/movies/{simkl_id}?extended=full`
- **TV/Anime:** `GET https://api.simkl.com/tv/{simkl_id}?extended=full`

**Example Full Response (Movie):**
```json
{
  "title": "The Batman",
  "year": 2022,
  "ids": {
    "simkl": 636830,
    "slug": "the-batman",
    "imdb": "tt1877830",
    "tmdb": "414906"
  },
  "overview": "In his second year of fighting crime, Batman uncovers corruption in Gotham City...",
  "runtime": 176,
  "released": "2022-03-04",
  "tagline": "Unmask the truth.",
  "certification": "PG-13",
  "genres": ["Action", "Crime", "Drama"],
  "ratings": {
    "simkl": { "rating": 8.1, "votes": 12450 }
  },
  "poster": "63/636830_0",
  "fanart": "63/636830_1"
}
```

---

## Section 7 — Field Mapping Table
| MediaMetadata Field | API Response Path | Notes / Transformation |
| :--- | :--- | :--- |
| `ExternalId` | `simkl:{type}:{ids.simkl}` | Construction logic. |
| `Source` | `"simkl"` | Hardcoded. |
| `Title` | `title` | |
| `Overview` | `overview` | |
| `Year` | `year` | |
| `PosterUrl` | `poster` | Requires prefix: `https://simkl.in/posters/` + `{path}_m.jpg` |
| `BackdropUrl` | `fanart` | Requires prefix: `https://simkl.in/fanart/` + `{path}_medium.jpg` |
| `RuntimeMinutes` | `runtime` | |
| `Genres` | `genres` | List of strings. |
| `Cast` | `not available` | (Note: People data requires a separate `/people` call if available). |
| `Directors` | `not available` | |
| `Rating` | `ratings.simkl.rating` | Scale 0-10. |
| `ExtendedData` | `ids` | Full ID object (IMDB, TMDB, TVDB, MAL). |

---

## Section 8 — ExternalId Convention
- **Format:** `simkl:{type}:{id}` (e.g., `simkl:movie:636830`, `simkl:anime:12345`).
- **Parsing:** Split by colon.
- **Fix Match URL Regex:** `simkl\.com/(movies|tv|anime)/([^/]+)` to extract slug/ID.

---

## Section 9 — Image Handling
SIMKL returns partial paths like `63/636830_0`.
- **Poster:** `https://simkl.in/posters/` + `path` + `_m.jpg`
- **Fanart:** `https://simkl.in/fanart/` + `path` + `_medium.jpg`

---

## Section 10 — Rate Limiting
- **Limit:** 1,000 requests per 5 minutes.
- **Headers:** `X-RateLimit-Remaining`, `Retry-After`.
- **429 Logic:** Suspend for the duration specified in `Retry-After`.

---

## Section 11 — Scoring Strategy
1. **Exact ID Match (IMDB/TMDB):** +100 points.
2. **Exact Title Match:** +50 points.
3. **Exact Year Match:** +20 points.
4. **Fuzzy Title Match:** +25 points.
- **Min Threshold:** 60.

---

## Section 12 — MediaTypeSupport
This plugin is **restricted** to the following first-class types:
| MediaTypeName | SupportedFields | Priority |
| :--- | :--- | :--- |
| `movie` | `title, overview, year, runtime, rating, genres, poster, backdrop` | 10 |
| `tv` | `title, overview, year, runtime, rating, genres, poster, backdrop` | 10 |
| `anime` | `title, overview, year, runtime, rating, genres, poster, backdrop` | 10 |

---

## Section 13 — Edge Cases
- **Anime vs TV:** Some shows exist in both. Use the specific `MediaType` from the request to choose the endpoint (`/search/anime` vs `/search/tv`).
- **MAL ID:** For Anime, ensure `ids.mal` is stored in `ExtendedData` as it is a critical ID for that type.
