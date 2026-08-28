# Chronicle Plugin Specification: Fanart.tv (V3)

This document provides a complete implementation specification for the Fanart.tv metadata plugin for Chronicle. Fanart.tv is primarily an image enrichment provider, supplying high-quality posters, backdrops, logos, and music-specific imagery.

---

## Section 1 — Service Overview
**Service Name:** Fanart.tv  
**Website:** [https://fanart.tv](https://fanart.tv)

Fanart.tv is a community-driven database of high-quality artwork. Unlike general metadata providers, it focuses exclusively on visual assets.

**Chronicle Media Types Supported:**
- `movie` (Level 0)
- `tv` (Level 0)
- `music_artist` (Level 0)
- `music_album` (Level 0)

**Availability:** This plugin should be available as a metadata provider for **Movies, TV, Music Artists, and Music Albums**.

---

## Section 2 — Authentication & Credential Acquisition
**Mechanism:** API Key (and optional Client Key) passed as a query parameter or header.
**Required Credentials:**
- `api_key`: Personal API Key.
- `client_key`: (Optional) User's personal project key for higher rate limits.

**Acquisition:**
1. Register at [Fanart.tv](https://fanart.tv).
2. Generate an API Key in the user settings dashboard.

---

## Section 3 — Plugin Settings Schema
| Key | Label | Description | Type | Required | Default |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `api_key` | Fanart.tv API Key | Your personal API key. | Password | True | null |
| `client_key` | Client Key | Optional client key for priority access. | Password | False | null |

---

## Section 4 — Manifest Values
**`manifest.json`**
```json
{
  "plugin_id": "chronicle.plugin.fanart",
  "name": "Fanart.tv",
  "version": "1.0.0",
  "author": "Chronicle Contributors",
  "description": "High-quality posters, backdrops, and logos for movies, TV, and music.",
  "min_chronicle_version": "1.0.0",
  "entry_type": "Chronicle.Plugin.Fanart.FanartMetadataProvider",
  "iconUrl": "https://fanart.tv/favicon.ico",
  "brandColorLight": "#FF8C00",
  "brandColorDark": "#E67E00",
  "fixMatchHint": "Enter a TMDB ID (movies), TVDB ID (TV), or MusicBrainz ID (music).",
  "supported_media_types": ["movie", "tv", "music_artist", "music_album"]
}
```

---

## Section 5 — Search Endpoint Specification
**Important:** Fanart.tv does **not** support text-based searching (e.g., "Batman"). It operates purely on IDs from other databases (TMDB, TVDB, MusicBrainz).
- **SearchAsync Logic:** If `context.ExternalId` contains a compatible ID (TMDB/TVDB/MBID), use that. Otherwise, return empty results.

---

## Section 6 — Fetch-by-ID Endpoint Specification
The API structure varies slightly by category.
- **Movies:** `GET https://webservice.fanart.tv/v3/movies/{tmdb_id}?api_key={api_key}`
- **TV:** `GET https://webservice.fanart.tv/v3/tv/{tvdb_id}?api_key={api_key}`
- **Music:** `GET https://webservice.fanart.tv/v3/music/{mbid}?api_key={api_key}`

**Example Response (TV - Breaking Bad):**
```json
{
    "name": "Breaking Bad",
    "thetvdb_id": "75682",
    "tvposter": [
        {
            "id": "12345",
            "url": "https://assets.fanart.tv/fanart/tv/75682/tvposter/breaking-bad-506085.jpg",
            "lang": "en",
            "likes": "10"
        }
    ],
    "showbackground": [
        {
            "id": "67890",
            "url": "https://assets.fanart.tv/fanart/tv/75682/showbackground/breaking-bad-506086.jpg",
            "lang": "en",
            "likes": "15"
        }
    ]
}
```

---

## Section 7 — Field Mapping Table
Fanart.tv fields are mapped to Chronicle's image properties. Use the item with the highest "likes" for the default.

| MediaMetadata Field | API Response Path | Notes |
| :--- | :--- | :--- |
| `ExternalId` | `fanart:{type}:{id}` | |
| `Title` | `name` | |
| `PosterUrl` | `movieposter[0].url` / `tvposter[0].url` | Filter by `lang == "en"` or user pref. |
| `BackdropUrl` | `moviebackground[0].url` / `showbackground[0].url` | |
| `AdditionalData["logos"]` | `movielogo` / `hdtvlogo` | List of logo URLs. |
| `AdditionalData["disc_art"]` | `moviedisc` / `cdart` | List of disc art URLs. |

---

## Section 8 — ExternalId Convention
- **Format:** `fanart:{type}:{id}` (e.g., `fanart:movie:414906`, `fanart:tv:75682`).
- **Parsing:** Split by colon to identify the target endpoint and external database ID.

---

## Section 9 — Image Handling
- Fanart.tv provides **full, absolute URLs**. No prefixing is required.
- **Selection Logic:** Sort arrays by `likes` (descending) and `lang` (preferring "en" or the system default) before selecting the primary URL.

---

## Section 10 — Rate Limiting
- **Limit:** 2 requests per second (without Client Key) or as specified by the API.
- **Error Handling:** Catch `429` status codes and implement a short cooldown period.

---

## Section 11 — Scoring Strategy
Since this plugin relies on hard IDs (TMDB/TVDB/MBID) for lookups:
1. **Successful ID Lookup:** +100 points (Instant Accept).
2. **Title Match (Backup Check):** +20 points.
- **Min Threshold:** 90.

---

## Section 12 — MediaTypeSupport
| MediaTypeName | SupportedFields | Priority |
| :--- | :--- | :--- |
| `movie` | `poster, backdrop, title` | 15 (High for images) |
| `tv` | `poster, backdrop, title` | 15 |
| `music_artist` | `backdrop, title` | 15 |
| `music_album` | `poster, title` | 15 |

---

## Section 13 — Edge Cases
- **Missing Languages:** If no "en" (English) assets are found, fall back to the asset with the most "likes" regardless of language.
- **Music Identifiers:** Music lookups **must** use MusicBrainz IDs (MBID).
