# Chronicle Plugin Specification: Trakt.tv (V3)

This document provides a complete implementation specification for the Trakt.tv metadata plugin for Chronicle. It is designed to be used by a developer to write a .NET 9 class library without further API research.

---

## Section 1 — Service Overview
**Service Name:** Trakt.tv  
**Website:** [https://trakt.tv](https://trakt.tv)

Trakt.tv is a comprehensive platform for tracking TV shows and movies. It aggregates metadata from sources like TMDB and TVDB and provides deep integration for user watch history, ratings, and community lists.

**Chronicle Media Types Supported:**
- `movie` (Hierarchy Level 0)
- `tv` (Hierarchy Level 0)
- `season` (Hierarchy Level 1)
- `episode` (Hierarchy Level 2)

**API Access:**
- Requires a **Client ID** (API Key).
- Registration is required via the Trakt Dashboard.
- **Tier:** Free for personal use. Rate limited (1,000 requests per 5 minutes).

**Official Documentation:** [https://trakt.docs.apiary.io/](https://trakt.docs.apiary.io/)

---

## Section 2 — Authentication & Credential Acquisition
**Mechanism:** Header-based Authentication.
**Required Headers:**
- `Content-Type: application/json`
- `trakt-api-version: 2`
- `trakt-api-key: {CLIENT_ID}`

**Step-by-Step Acquisition:**
1. Login to [Trakt.tv](https://trakt.tv).
2. Navigate to [Settings > API Applications](https://trakt.tv/oauth/applications).
3. Click **"New Application"**.
4. Name it "Chronicle" and provide a placeholder redirect URI (e.g., `http://localhost`).
5. Save and copy the **Client ID**.

---

## Section 3 — Plugin Settings Schema
| Key | Label | Description | Type | Required | Default |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `client_id` | API Client ID | Your Trakt.tv API Client ID from the developer portal. | Password | True | null |

---

## Section 4 — Manifest Values
**`manifest.json`**
```json
{
  "plugin_id": "chronicle.plugin.trakt",
  "name": "Trakt.tv",
  "version": "1.0.0",
  "author": "Chronicle Contributors",
  "description": "Fetch movie and TV metadata, ratings, and cast from Trakt.tv.",
  "min_chronicle_version": "1.0.0",
  "entry_type": "Chronicle.Plugin.Trakt.TraktMetadataProvider",
  "iconUrl": "https://trakt.tv/favicon.ico",
  "brandColorLight": "#ED1C24",
  "brandColorDark": "#FF4D4D",
  "fixMatchHint": "Enter Trakt ID (e.g. 1234), IMDB ID (tt1234), or Trakt URL.",
  "background_tasks": [
    {
      "task_id": "fetch-missing-metadata",
      "display_name": "Fetch Missing Metadata",
      "description": "Looks up metadata for items missing Trakt IDs.",
      "default_cron": "0 4 * * *",
      "default_enabled": true
    }
  ]
}
```

---

## Section 5 — Search Endpoint Specification
### Endpoint: Text Query Search
- **Media Types:** `movie`, `tv`
- **HTTP Method:** `GET`
- **URL:** `https://api.trakt.tv/search/{type}?query={query}&extended=full`
- **Query Parameters:**
  - `query`: The URL-encoded string from `context.Name`.
  - `extended`: `full` (required to get plot and runtime).
  - `years`: (Optional) Use `context.Year`.

**Example Request:**
`GET https://api.trakt.tv/search/movie?query=batman&years=2022&extended=full`

**Example Response:**
```json
[
  {
    "type": "movie",
    "score": 100.0,
    "movie": {
      "title": "The Batman",
      "year": 2022,
      "ids": {
        "trakt": 348356,
        "slug": "the-batman-2022",
        "imdb": "tt1877830",
        "tmdb": 414906
      },
      "tagline": "Unmask the truth.",
      "overview": "In his second year of fighting crime, Batman uncovers corruption in Gotham City that connects to his own family while facing a serial killer known as the Riddler.",
      "released": "2022-03-04",
      "runtime": 176,
      "country": "us",
      "updated_at": "2024-04-10T08:37:30.000Z",
      "trailer": "https://youtube.com/watch?v=mqqft2E_V4A",
      "homepage": "https://www.thebatman.com",
      "status": "released",
      "rating": 8.0123,
      "votes": 45000,
      "comment_count": 120,
      "language": "en",
      "available_translations": ["en", "fr", "de"],
      "genres": ["action", "crime", "drama"],
      "certification": "PG-13"
    }
  }
]
```

---

## Section 6 — Fetch-by-ID Endpoint Specification
### 1. Movie / Show Summary
- **URL Template:** `GET https://api.trakt.tv/{type}s/{id}?extended=full`

### 2. People (Cast & Directors)
- **URL Template:** `GET https://api.trakt.tv/{type}s/{id}/people`
- **Example Response:**
```json
{
  "cast": [
    { "character": "Bruce Wayne / Batman", "person": { "name": "Robert Pattinson", "ids": { "trakt": 2724, "slug": "robert-pattinson" } } },
    { "character": "Selina Kyle / Catwoman", "person": { "name": "Zoë Kravitz", "ids": { "trakt": 93259, "slug": "zoe-kravitz" } } }
  ],
  "crew": {
    "directing": [
      { "job": "Director", "person": { "name": "Matt Reeves", "ids": { "trakt": 3574, "slug": "matt-reeves" } } }
    ]
  }
}
```

### 3. Season / Episode
- **Season URL:** `GET https://api.trakt.tv/shows/{id}/seasons?extended=full`
- **Episode URL:** `GET https://api.trakt.tv/shows/{id}/seasons/{season}/episodes/{episode}?extended=full`

---

## Section 7 — Field Mapping Table
| MediaMetadata Field | API Response Path | Notes / Transformation |
| :--- | :--- | :--- |
| `ExternalId` | `trakt:{type}:{ids.trakt}` | Construction logic. |
| `Source` | `"trakt"` | Hardcoded. |
| `Title` | `title` | |
| `Overview` | `overview` | |
| `Year` | `year` | |
| `PosterUrl` | `not available` | |
| `BackdropUrl` | `not available` | |
| `RuntimeMinutes` | `runtime` | |
| `Genres` | `genres` | Array |
| `Cast` | `cast[].person.name` | Top 10 from `/people`. |
| `Directors` | `crew.directing[?(@.job=='Director')].person.name` | From `/people`. |
| `Rating` | `rating` | Scale 0-10. |
| `ExtendedData` | `ids` | Full ID object. |

---

## Section 8 — ExternalId Convention
- **Format:** `trakt:{type}:{id}`
- **Parsing:** Split by colon.
- **Fix Match URL Regex:** `trakt\.tv/(movies|shows)/([^/]+)` to extract the slug.

---

## Section 9 — Image Handling
Trakt does not provide imagery. Set `PosterUrl` and `BackdropUrl` to `null`. Use the `tmdb` ID in `ExtendedData` for image enrichment via a secondary provider.

---

## Section 10 — Rate Limiting
- **Limit:** 1,000 requests / 5 minutes.
- **Header:** `X-Ratelimit` and `Retry-After`.
- **Logic:** Implement exponential backoff.

---

## Section 11 — Scoring Strategy
1. **Exact ID Match (IMDB/TMDB):** +100 points.
2. **Exact Title Match:** +50 points.
3. **Exact Year Match:** +20 points.
4. **Fuzzy Title (Levenshtein < 2):** +25 points.
- **Min Match Score:** 60.

---

## Section 12 — MediaTypeSupport
| MediaTypeName | SupportedFields | Priority |
| :--- | :--- | :--- |
| `movie` | `title, overview, year, runtime, rating, genres, cast, directors` | 10 |
| `tv` | `title, overview, year, runtime, rating, genres, cast, directors` | 10 |
| `season` | `title, overview, year, rating` | 10 |
| `episode` | `title, overview, year, runtime, rating` | 10 |
