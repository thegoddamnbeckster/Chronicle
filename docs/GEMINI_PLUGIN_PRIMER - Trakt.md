# Chronicle Plugin Authoring Primer

**Purpose:** Hand this document to Gemini (or any LLM) along with a target service name to produce a complete, implementable plugin specification. The output of that conversation should require zero additional API research to implement.

---

## Your Task

You will produce a **complete implementation specification** for a Chronicle metadata plugin for a specific service. The document you produce will be used directly by a developer to write the plugin in C# .NET 9. It must contain enough detail that no further API research is needed.

Chronicle is a self-hosted universal media tracking platform. Plugins are .NET 9 class libraries that implement the `IMetadataProvider` interface to fetch metadata from an external service. Chronicle's enrichment engine calls your plugin's `SearchAsync` and `GetByIdAsync` methods to populate media items in the database.

The target service is: **Trakt.tv**

---

## Part 1 — Chronicle Plugin Architecture (Study This Carefully)

Your specification must map the target service's API to these exact types. Read every field and its documentation comment.

### `IMetadataProvider` — The primary interface

```csharp
namespace Chronicle.Plugins;

/// <summary>
/// Implemented by metadata scraper plugins (TMDB, MusicBrainz, etc.).
/// All implementations must be stateless between calls — configuration is
/// supplied once via Configure() and then used for every request.
/// </summary>
public interface IMetadataProvider
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Unique reverse-domain plugin identifier. e.g. "chronicle.plugin.tmdb".</summary>
    string PluginId { get; }

    string Name    { get; }
    string Version { get; }
    string Author  { get; }

    // ── Capability declarations ───────────────────────────────────────────────

    /// <summary>Returns all media types this provider can supply metadata for.</summary>
    MediaTypeSupport[] GetSupportedMediaTypes();

    /// <summary>Returns the settings schema used to generate the configuration UI.</summary>
    PluginSettingsSchema GetSettingsSchema();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called once after instantiation with the user-supplied settings.
    /// Keys match SettingDefinition.Key values from the schema.
    /// </summary>
    void Configure(IReadOnlyDictionary<string, string> settings);

    // ── Core operations ───────────────────────────────────────────────────────

    /// <summary>
    /// Searches for media matching the context and returns scored candidates.
    /// The plugin is responsible for query construction, candidate retrieval,
    /// and scoring (0–100). Chronicle applies a confidence threshold to decide
    /// accept/reject (default threshold: 60).
    /// Return an empty list if no candidates found.
    /// </summary>
    Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches full metadata for the item identified by the provider's external id.
    /// The externalId format is whatever the plugin set on ScoredCandidate.Metadata.ExternalId.
    /// Throw an appropriate exception if not found.
    /// </summary>
    Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default);

    /// <summary>
    /// Downloads an image from the given URL and returns the raw bytes.
    /// Used for poster/backdrop caching.
    /// </summary>
    Task<byte[]> GetImageAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Verifies that the provider can reach its upstream service with the
    /// supplied credentials. Return true = healthy.
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}
```

### `MediaSearchContext` — What Chronicle passes into `SearchAsync`

```csharp
namespace Chronicle.Plugins.Models;

public record MediaSearchContext(
    /// <summary>Item name, pre-normalised by Chronicle (punctuation stripped, lowercased).</summary>
    string  Name,
    int?    Year              = null,
    /// <summary>Parent item name — artist for an album, show for a season.</summary>
    string? ParentName        = null,
    /// <summary>Grandparent item name — artist for a track.</summary>
    string? GrandparentName   = null,
    /// <summary>Position within parent — season number, track number, episode number.</summary>
    int?    ItemNumber        = null,
    /// <summary>
    /// Number of direct children already in Chronicle for this item.
    /// Allows structural validation: does the provider's season/track count match?
    /// </summary>
    int?    ChildCount        = null,
    /// <summary>0 = root (show/artist/movie), 1 = season/album, 2 = episode/track.</summary>
    int     HierarchyLevel   = 0,
    /// <summary>
    /// Precise title read directly from file metadata (e.g. NFO title element, audio tag).
    /// When present, use for exact case-insensitive comparison WITHOUT punctuation stripping.
    /// </summary>
    string? PreciseName      = null,
    /// <summary>
    /// Clean title from the source filename (extension/track-number stripped).
    /// Use as a fallback search term when Name returns zero results.
    /// </summary>
    string? FilenameStem     = null,
    /// <summary>
    /// Names of sibling items sharing the same parent (e.g. other tracks on same album).
    /// Use to disambiguate when title alone is ambiguous.
    /// </summary>
    IReadOnlyList<string>? SiblingNames = null,
    /// <summary>
    /// Ordered alternative title forms: [PreciseName?, year-stripped, FilenameStem?,
    /// version-qualifier-stripped?]. Try each in order when earlier ones return no results.
    /// </summary>
    IReadOnlyList<string>? AltTitles = null,
    /// <summary>Names of direct children (albums for artist, tracks for album, episodes for show).</summary>
    IReadOnlyList<string>? ChildNames = null,
    /// <summary>Structured metadata for sibling/child items for deep structural matching.</summary>
    IReadOnlyList<SiblingInfo>? SubItemMetadata = null
);
```

### `SiblingInfo` — Structured child/sibling metadata for deep matching

```csharp
namespace Chronicle.Plugins.Models;

public record SiblingInfo(
    string Name,
    int?   ItemNumber      = null,   // track/episode number
    int?   DiscNumber      = null,   // disc/season number
    int?   DurationSeconds = null,   // match tolerance ±10 s by default
    IReadOnlyDictionary<string, string>? Tags = null   // e.g. "isrc", "genre"
);
```

### `ScoredCandidate` — What `SearchAsync` must return

```csharp
namespace Chronicle.Plugins.Models;

public record ScoredCandidate(
    /// <summary>Full metadata for this candidate. Must have a non-empty ExternalId.</summary>
    MediaMetadata Metadata,
    /// <summary>Confidence score 0–100, plugin-computed.</summary>
    int           Score,
    /// <summary>Human-readable explanation: which signals fired and why.</summary>
    string?       ScoreReason = null
);
```

### `MediaMetadata` — The data model returned by `GetByIdAsync` (and inside `ScoredCandidate`)

```csharp
namespace Chronicle.Plugins.Models;

public class MediaMetadata
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>
    /// External ID in the provider's own namespace format.
    /// The format is defined by the plugin — Chronicle stores it opaquely.
    /// TMDB uses "movie:550" and "tv:1396". MusicBrainz uses
    /// "artist:{mbid}", "release-group:{mbid}", "recording:{mbid}".
    /// </summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>
    /// Source identifier matching the plugin (e.g. "tmdb", "musicbrainz").
    /// Used as the key in the media item's metadata_json column.
    /// Should match a lowercase short name for the service.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    // ── Core fields ───────────────────────────────────────────────────────────

    public string  Title          { get; set; } = string.Empty;
    public string? Overview       { get; set; }   // plot/description
    public int?    Year           { get; set; }   // release year
    public string? PosterUrl      { get; set; }   // primary poster image URL
    public string? BackdropUrl    { get; set; }   // wide/banner image URL
    public int?    RuntimeMinutes { get; set; }

    // ── Extended fields ───────────────────────────────────────────────────────

    public List<string> Genres    { get; set; } = [];
    public List<string> Cast      { get; set; } = [];        // actor names
    public List<string> Directors { get; set; } = [];
    public double?      Rating    { get; set; }              // 0.0–10.0 scale

    /// <summary>Community/folksonomy tags beyond the curated Genres list.</summary>
    public List<string> Tags      { get; set; } = [];

    /// <summary>
    /// Additional images beyond PosterUrl/BackdropUrl.
    /// (e.g. back cover, booklet, CD tray, episode stills, fanart)
    /// </summary>
    public List<AdditionalImage> AdditionalImages { get; set; } = [];

    /// <summary>
    /// Provider-specific structured data that doesn't map to any field above.
    /// Stored as raw JSON in the media item's metadata_json column, keyed by plugin_id.
    /// Use this for anything valuable that has no generic home — track listings,
    /// label info, ISRCs, original language, production companies, etc.
    /// </summary>
    public JsonElement? ExtendedData { get; set; }

    // ── Search-mode fields ────────────────────────────────────────────────────

    /// <summary>Populated only in search results (SearchAsync), not in GetByIdAsync.</summary>
    public List<MediaMetadata> Results { get; set; } = [];
    public int TotalResults            { get; set; }
}

public class AdditionalImage
{
    public string  Url          { get; set; } = string.Empty;
    /// <summary>Label: "Front", "Back", "Booklet", "Medium", "Spine", "Obi", "Tray", "Still", etc.</summary>
    public string? Type         { get; set; }
    public string? ThumbnailUrl { get; set; }
}
```

### `MediaTypeSupport` — Declares which Chronicle media types the plugin handles

```csharp
namespace Chronicle.Plugins.Models;

public class MediaTypeSupport
{
    /// <summary>
    /// Media type name as stored in the Chronicle database.
    /// Valid values: "movie", "tv", "music", "album", "track",
    ///               "book", "audiobook", "podcast", "game", "fanedits"
    /// (plus any custom types the user has added)
    /// </summary>
    public string MediaTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Metadata fields this plugin can populate for this type.
    /// Must use the field names from the Metadata Assignment system:
    /// "title", "overview", "year", "poster_url", "backdrop_url",
    /// "runtime_minutes", "rating", "genres", "cast", "directors", "tags"
    /// </summary>
    public List<string> SupportedFields { get; set; } = [];

    /// <summary>Lower = higher priority when multiple providers support the same type. Default: 10.</summary>
    public int DefaultPriority { get; set; } = 10;
}
```

### `PluginSettingsSchema` + `SettingDefinition` — Configuration UI definition

```csharp
namespace Chronicle.Plugins.Models;

public class PluginSettingsSchema
{
    public List<SettingDefinition> Settings { get; set; } = [];
}

public class SettingDefinition
{
    public string  Key          { get; set; } = string.Empty;  // programmatic key
    public string  Label        { get; set; } = string.Empty;  // UI label
    public string? Description  { get; set; }                  // help text below field
    public SettingType Type     { get; set; } = SettingType.Text;
    public bool    Required     { get; set; }
    public string? DefaultValue { get; set; }
    public List<SelectOption> Options { get; set; } = [];      // for Dropdown/MultiSelect
}

public class SelectOption
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public enum SettingType
{
    Text,        // single-line text
    Password,    // masked input, stored encrypted
    Number,
    Boolean,
    Dropdown,    // single-select from Options list
    MultiSelect, // multi-select from Options list
    Url,
    FilePath,
    TextArea
}
```

### `manifest.json` schema — Shipped alongside the plugin DLL

```json
{
  "plugin_id":            "chronicle.plugin.servicename",
  "name":                 "Service Display Name",
  "version":              "1.0.0",
  "author":               "Chronicle Contributors",
  "description":          "One-sentence description for the Plugins page.",
  "min_chronicle_version": "1.0.0",
  "entry_type":           "Chronicle.Plugin.ServiceName.ServiceNameMetadataProvider",
  "iconUrl":              "https://example.com/favicon.ico",
  "brandColorLight":      "#RRGGBB",
  "brandColorDark":       "#RRGGBB",
  "fixMatchHint":         "Enter a [ServiceName] ID (e.g. 12345) or URL",
  "background_tasks": [
    {
      "task_id":         "fetch-missing-metadata",
      "display_name":    "Fetch Missing Metadata",
      "description":     "Looks up metadata for items that don't have it yet.",
      "default_cron":    "0 4 * * *",
      "default_enabled": true
    },
    {
      "task_id":         "resync-all-metadata",
      "display_name":    "Re-sync All Metadata",
      "description":     "Re-downloads all metadata to pick up updates.",
      "default_cron":    "0 3 * * 0",
      "default_enabled": false
    }
  ]
}
```

**Well-known `task_id` values** — these get Chronicle's built-in execution engine for free:
- `"fetch-missing-metadata"` — runs enrichment for items with no ExternalId for this plugin
- `"resync-all-metadata"` — re-runs enrichment for all items this plugin supports

---

## Part 2 — How Chronicle Uses Plugins

Understanding this is essential for designing a correct scoring strategy.

### Enrichment flow

1. Chronicle identifies media items that lack metadata for a plugin.
2. For each item, it builds a `MediaSearchContext` from the item's name, year, hierarchy level, and any file scanner signals.
3. It calls `SearchAsync(context)` and receives a list of `ScoredCandidate` objects.
4. The candidate with the highest score is selected **if** its score meets the confidence threshold (default: 60/100). Lower-scoring results are stored as diagnostics but not applied.
5. If accepted, Chronicle calls `GetByIdAsync(candidate.Metadata.ExternalId)` to fetch the full record.
6. The returned `MediaMetadata` is written to the item's `metadata_json` column under the plugin's `plugin_id` key.

### Hierarchy levels

Chronicle items have three levels:
- **Level 0**: Root items — movies, TV shows, artists, books, audiobooks
- **Level 1**: Mid-level — seasons (TV), albums (music), volumes (books)
- **Level 2**: Leaf items — episodes, tracks, chapters

`SearchAsync` is called at every level. The `MediaSearchContext` includes `ParentName`, `GrandparentName`, `ItemNumber`, and `ChildCount` so the plugin can construct precise queries (e.g. search for track "X" on album "Y" by artist "Z").

### Scoring guidelines

Scores are 0–100. Chronicle applies a default threshold of 60. Design your scoring so that:
- An exact title match with year match scores ≥ 85
- An exact title match without year scores ≥ 70
- A fuzzy title match (close but not identical) scores 50–69
- A weak or uncertain match scores < 50

Return all plausible candidates, not just the top one — Chronicle displays the full list as diagnostics.

### ExternalId format convention

Chronicle stores ExternalIds opaquely. The format is entirely up to the plugin, but must be:
- Unambiguous: distinguishes between different entity types (movie vs TV show, artist vs album)
- URL-safe: no characters that break path segments
- Stable: the same item always produces the same ExternalId

Example conventions:
- TMDB: `movie:550`, `tv:1396`, `season:1396:1`, `episode:1396:1:3`
- MusicBrainz: `artist:{mbid}`, `release-group:{mbid}`, `recording:{mbid}`

### `GetByIdAsync` contract

The `externalId` passed in will be exactly what was set on `ScoredCandidate.Metadata.ExternalId` during search, or whatever the user typed in the Fix Match panel. Your implementation must:
- Parse the format it defined
- Handle user-pasted URLs from the service's website (strip to ID)
- Throw a meaningful exception (`NotFoundException` or similar) for IDs not found
- Return a fully-populated `MediaMetadata` — this is what gets stored

---

## Part 3 — Required Output Template

Produce a document containing **all** of the following sections. Each section has explicit completeness requirements. Do not summarise, abbreviate, or use pseudocode.

---

### Section 1 — Service Overview

Provide:
- Full service name and canonical website URL
- One-paragraph description of what the service indexes
- Which Chronicle media types this service can serve (from the valid list: `movie`, `tv`, `music`, `album`, `track`, `book`, `audiobook`, `podcast`, `game` — only those the service genuinely covers)
- Whether API access requires registration, an API key, OAuth, or is open
- Whether the API is free, freemium, or paid — include any relevant tier limits
- Link to the official API documentation

---

### Section 2 — Authentication & Credential Acquisition

Provide:
- Authentication mechanism: API key in header, query param, Bearer token, OAuth 2.0, or none required
- Exact header name or query parameter name (e.g. `Authorization: Bearer {token}`, `?api_key={key}`)
- Step-by-step instructions for obtaining credentials (register at URL, create app, copy key)
- Whether credentials expire or need rotation
- Any special requirements (e.g. user-agent string must be set, Terms of Service must be accepted)

---

### Section 3 — Plugin Settings Schema

List every setting the plugin needs to expose to the user. For **each** setting provide:
- `Key` (the programmatic identifier, e.g. `"api_key"`)
- `Label` (human-readable UI label, e.g. `"API Key"`)
- `Description` (help text shown beneath the field)
- `Type` (one of: Text, Password, Number, Boolean, Dropdown, MultiSelect, Url, FilePath, TextArea)
- `Required` (true/false)
- `DefaultValue` (or null if none)
- For Dropdown/MultiSelect: the full list of options with value and label

---

### Section 4 — Manifest Values

Provide exact values for every `manifest.json` field:
- `plugin_id` — following the `chronicle.plugin.{servicename}` convention
- `name` — display name as shown in the Chronicle UI
- `version` — starting version string
- `description` — one sentence for the Plugins page
- `entry_type` — fully-qualified C# class name
- `iconUrl` — direct URL to the service's favicon or a small square logo
- `brandColorLight` — hex colour for light mode (the service's primary brand colour)
- `brandColorDark` — hex colour for dark mode (lighter variant suitable on dark backgrounds)
- `fixMatchHint` — the hint text shown in the Fix Match panel explaining what the user should enter, including examples in the formats the plugin's `GetByIdAsync` accepts

---

### Section 5 — Search Endpoint Specification

For **each** search endpoint the plugin needs (there may be different endpoints per media type):

1. **Media types covered** by this endpoint
2. **HTTP method and full URL** (with base URL separate from path)
3. **Query parameters**: name, type, required/optional, what value Chronicle should supply, any constraints
4. **Request headers** required
5. **A complete, real example request** — full URL with actual parameter values filled in
6. **A complete, real example response** — the full JSON as returned by the API, not abbreviated. Use `...` only for very long arrays (but include at least 2 full array elements). **This is the most important part of this section** — the implementer needs to see the exact field names, nesting, and types.
7. **Pagination**: does this endpoint paginate? Which parameter controls the page? Which response field contains total results?
8. **Error responses**: what does the API return for invalid credentials, rate limit exceeded, or no results? Include example error response JSON.

---

### Section 6 — Fetch-by-ID Endpoint Specification

For **each** entity type the plugin supports (movie, TV show, season, episode, artist, album, track, etc.):

1. **Entity type** and corresponding Chronicle hierarchy level
2. **HTTP method and full URL template** — show the ID substitution clearly (e.g. `GET https://api.example.com/v1/movies/{id}`)
3. **Request headers** required
4. **A complete, real example request**
5. **A complete, real example response** — full JSON. Do not abbreviate. This is what `GetByIdAsync` must parse.
6. **Which fields in this response map to each `MediaMetadata` field** — see Section 7

---

### Section 7 — Field Mapping Table

For **each** Chronicle `MediaMetadata` field, specify exactly how to populate it from the API response. Use dot-notation for nested fields (e.g. `response.movie.release_date`).

| MediaMetadata Field  | API Response Path                          | Notes / Transformation Required            |
|----------------------|--------------------------------------------|--------------------------------------------|
| `ExternalId`         |                                            | Include full format string, e.g. `"movie:{response.id}"` |
| `Source`             | (hardcoded)                                | The short service name, e.g. `"tmdb"`      |
| `Title`              |                                            |                                            |
| `Overview`           |                                            |                                            |
| `Year`               |                                            | If a full date, extract year component     |
| `PosterUrl`          |                                            | If URL construction needed, show formula   |
| `BackdropUrl`        |                                            | If URL construction needed, show formula   |
| `RuntimeMinutes`     |                                            |                                            |
| `Genres`             |                                            | If array of objects, specify which subfield|
| `Cast`               |                                            | Actor names only, specify order/limit      |
| `Directors`          |                                            |                                            |
| `Rating`             |                                            | Note the original scale and normalise to 0–10 if needed |
| `Tags`               |                                            |                                            |
| `AdditionalImages`   |                                            | List all image types the API provides; specify `Type` label for each |
| `ExtendedData`       |                                            | List every additional field worth preserving — track listings, ISRCs, label, original language, production companies, etc. |

**Important:** If a `MediaMetadata` field has no equivalent in this service's API, write `"not available"` explicitly. Do not leave rows blank.

---

### Section 8 — ExternalId Convention

Define the complete ExternalId format for **every** entity type the plugin handles:
- Show the exact format string (e.g. `"season:{show_id}:{season_number}"`)
- Show how `GetByIdAsync` parses it back to the API call
- Explain how the plugin handles a user pasting a full URL from the service's website into the Fix Match panel — what URL patterns does it need to recognise and parse?

---

### Section 9 — Image Handling

Many services return image paths rather than full URLs. For each image type:
- Is it a full URL or a path that needs a base URL prepended?
- What is the base URL?
- Are size variants available? If so, what are the valid size identifiers and which should be used for `PosterUrl` (high-res) vs `ThumbnailUrl` (fast preview)?
- Which image types map to `PosterUrl`, `BackdropUrl`, and `AdditionalImages`?
- If the service has a Cover Art Archive or separate image service, describe that endpoint separately

---

### Section 10 — Rate Limiting & Error Handling

Provide:
- **Rate limit**: exact requests-per-second or requests-per-day limit (if documented)
- **Rate limit response**: HTTP status code and example response body when the limit is exceeded
- **Recommended retry strategy**: delay between retries, max retries, exponential backoff or fixed?
- **Authentication error**: HTTP status and response body for invalid credentials
- **Not Found**: HTTP status and response body when an ID doesn't exist
- **Other common errors**: list any API-specific error codes the plugin should handle gracefully
- **Timeout recommendation**: suggested `HttpClient` timeout in seconds for this service

---

### Section 11 — Scoring Strategy

Describe a complete scoring algorithm for `SearchAsync` for each media type/hierarchy level.

For **each** combination of media type and hierarchy level:
1. Which fields from `MediaSearchContext` are relevant?
2. Which fields from the search result candidate are compared?
3. How many points does each signal contribute to the 0–100 score? (Be specific — e.g. "exact title match: +50, year match: +20, fuzzy title: +30")
4. What is the minimum score that indicates a genuine match (vs noise)?
5. Are there any hard-reject conditions (e.g. wrong media type, year off by more than 5)?

---

### Section 12 — `MediaTypeSupport` Declaration

For each Chronicle media type this plugin supports, provide the complete `MediaTypeSupport` object values:
- `MediaTypeName` (exact string — must match Chronicle's database values)
- `SupportedFields` (list every field from `MediaMetadata` this plugin can populate, using the field names from Section 7)
- `DefaultPriority` (suggested value: 10 for general-purpose plugins)

---

### Section 13 — Edge Cases & Known Quirks

Document everything that would cause a naive implementation to fail:
- Fields that are sometimes null, sometimes missing, sometimes empty string
- Pagination gotchas (off-by-one page numbers, cursor vs offset, max page size)
- API responses that differ between search results and full fetches (e.g. search returns minimal data)
- Any data quality issues known to affect this service (e.g. incorrect years, missing images for older content)
- Search relevance issues — does the API rank by popularity by default? Does a niche item appear on page 5?
- Services that require a `User-Agent` header with contact info (MusicBrainz requires this)
- Any fields that are available in one media type endpoint but not another
- Regional availability — does the API behave differently based on IP or language parameter?
- Anything else a developer would only discover after spending hours debugging

---

## Part 4 — Quality Rules

These rules are mandatory. A response that violates them is incomplete.

1. **No invented JSON.** All example request/response JSON must be real API responses from the actual service. If you cannot produce a real example, state that clearly and explain what the response structure looks like based on documentation.

2. **No abbreviation.** Write `...` only for very long arrays, and only after showing at least two full elements. Never abbreviate objects.

3. **Exact dot-notation paths.** Every field reference must be a complete dot-notation path from the response root (e.g. `data.results[0].release_dates.results[0].release_date`), not a vague description like "the release date field".

4. **Every MediaMetadata field must appear in the mapping table.** Fields with no equivalent must be marked `"not available"` — not omitted.

5. **Scoring must be numeric.** Every signal in the scoring strategy must have a specific point value. "Title match scores highly" is not acceptable. "Exact title match: +50 points" is acceptable.

6. **Settings must be complete.** Every `SettingDefinition` must include all fields: Key, Label, Description, Type, Required, DefaultValue.

7. **ExternalId format must be parseable.** Show both how the ID is constructed and how it is parsed back in `GetByIdAsync`. Show the URL-to-ID extraction for Fix Match.

8. **No "see documentation" deferrals.** If something requires checking the API docs, check them and include the result inline. The specification must be self-contained.
