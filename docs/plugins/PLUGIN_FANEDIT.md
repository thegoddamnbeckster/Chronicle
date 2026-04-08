# Chronicle.Plugin.FanEdit

Metadata source plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) that
fetches fanedit metadata from [fanedit.org (IFDB – the Internet Fan Edit Database)](https://www.fanedit.org/ifdb/).

**Plugin ID:** `chronicle.plugin.fanedit`
**Version:** 1.0.0
**Media Types:** Movies (`movies`)
**Auth:** fanedit.org username + password (login required)
**Data source:** fanedit.org – HTML scraping (no public API)

---

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Authentication Flow](#authentication-flow)
- [Rate Limiting Strategy](#rate-limiting-strategy)
- [Data Model](#data-model)
- [Scraping Approach](#scraping-approach)
- [Settings Schema](#settings-schema)
- [manifest.json](#manifestjson)
- [Background Tasks](#background-tasks)
- [Error Handling](#error-handling)
- [Repository Structure](#repository-structure)
- [Building & Packaging](#building--packaging)
- [Branding Reference](#branding-reference)

---

## Overview

[fanedit.org](https://www.fanedit.org) hosts the Internet Fan Edit Database (IFDB), a
community-run catalogue of fan-edited versions of movies and other media. Fan edits are
reworked cuts of existing films – they may be extended editions, colour-graded versions,
TV-safe cuts, or entirely custom re-edits. Each IFDB entry is a rich record describing the
edit itself, its relationship to the source material, the editor who created it, and community
reception.

fanedit.org is a small, community-maintained site. It is emphatically **not** an API service
and was not designed to be crawled. This plugin is built around strict respect for the server:
all HTTP requests are throttled to a minimum of one second apart, and the plugin is intended
for personal, single-user use only. Treat fanedit.org the way you would treat any website
you genuinely care about.

Because fanedit.org requires a registered account to browse the full catalogue, credentials
(username and password) must be provided by the user before the plugin can operate.

---

## Architecture

This plugin implements `IMetadataProvider` from `Chronicle.Plugins`. It is a **scraping
provider**: there is no public API, so all data is extracted from the HTML returned by the
site's pages. Authentication is handled by posting to the site's login form and persisting
the resulting session cookie for the lifetime of the configured plugin instance.

```
Chronicle Host
    │
    ├── Configure(settings)        ← username + password stored; session cookie cleared
    │
    ├── HealthCheckAsync()         ← attempt login; return true if session established
    │
    ├── SearchAsync(context)       ← GET /ifdb/ search results page; parse candidate list
    │       └─ returns scored candidates with ExternalId = "fanedit:{ifdb-id}"
    │
    ├── GetByIdAsync(externalId)   ← GET /ifdb/{slug}/ detail page; parse full record
    │       └─ returns MediaMetadata with all fields + ExtendedData
    │
    └── GetImageAsync(url)         ← Chronicle calls this if it needs raw image bytes
            └─ GET {image-url} (direct, with session cookie; respects rate limit)
```

### Stateless Design

In keeping with Chronicle's plugin contract, `FanEditMetadataProvider` is **stateless between
Configure calls**. The session cookie is obtained lazily on the first request after
`Configure` is called and is stored in an instance field – not in the database. If the
cookie expires mid-run, the plugin transparently re-authenticates and retries the request
once before surfacing an error.

### Dependency Overview

| Dependency | Purpose |
|---|---|
| `HtmlAgilityPack` | HTML parsing / XPath queries against page DOM |
| `System.Net.Http.HttpClient` | HTTP transport (injected via `IHttpClientFactory`) |
| No additional NuGet packages required | fanedit.org returns plain HTML |

---

## Authentication Flow

fanedit.org uses a standard HTML form login. The plugin performs a two-step handshake to
obtain a session cookie before any catalogue requests.

### Step 1 – Fetch the login page (CSRF token)

```
GET https://www.fanedit.org/wp-login.php
```

The response contains a WordPress `_wpnonce` (or equivalent hidden field) that must be
echoed back in the POST. Parse the value from the login form's hidden input.

### Step 2 – POST credentials

```
POST https://www.fanedit.org/wp-login.php
Content-Type: application/x-www-form-urlencoded

log={username}&pwd={password}&wp-submit=Log+In&redirect_to=%2F&testcookie=1
```

A successful login results in an HTTP 302 redirect and sets one or more session cookies
(typically `wordpress_logged_in_*` and `wordpress_sec_*`). These cookies must be attached
to every subsequent request via a shared `CookieContainer`.

If the response does not set any `wordpress_logged_in_*` cookie, treat login as failed and
surface a descriptive error to Chronicle.

### Session Lifecycle

- The session cookie is valid for the duration of the WordPress session (typically several
  weeks with the default remember-me lifetime).
- The plugin tracks a `_sessionEstablished` flag. If a subsequent request returns an
  HTTP 302 to the login page, the session has expired: clear the flag and re-authenticate
  before retrying the original request exactly once.
- Login credentials are never logged at any level.

### Health Check

`HealthCheckAsync` performs the full two-step login against the live site and returns
`true` if a session cookie is obtained. No catalogue pages are fetched during the health
check. This is the action behind Chronicle's **HEALTHY / UNHEALTHY** badge on the Plugins
page.

```
HealthCheckAsync → POST login → got session cookie? → true : false
```

---

## Rate Limiting Strategy

fanedit.org is a small community site. It is not designed to serve automated traffic.
The plugin enforces a strict, per-instance rate limit on **every outbound HTTP request**,
including the login handshake, search requests, detail page fetches, and image fetches.

### Rules

| Rule | Value | Rationale |
|---|---|---|
| Minimum inter-request gap | **1,000 ms** (configurable, floor enforced) | Ensures at most 1 request/second |
| Minimum floor (absolute) | **1,000 ms** | Hard-coded lower bound; the user cannot set it lower |
| Implementation | `SemaphoreSlim(1,1)` + `Stopwatch` elapsed check | Serialises all requests; no burst possible |
| Applies to | ALL requests (login, search, detail, image) | No request is exempt |

### Implementation Pattern

```csharp
private readonly SemaphoreSlim _rateLimitGate = new(1, 1);
private Stopwatch _lastRequestStopwatch = Stopwatch.StartNew();
private int _delayMs; // populated from settings; clamped to >= 1000

private async Task ThrottleAsync(CancellationToken ct)
{
    await _rateLimitGate.WaitAsync(ct);
    try
    {
        var elapsed = _lastRequestStopwatch.ElapsedMilliseconds;
        if (elapsed < _delayMs)
            await Task.Delay((int)(_delayMs - elapsed), ct);

        _lastRequestStopwatch.Restart();
    }
    finally
    {
        _rateLimitGate.Release();
    }
}
```

Every method that issues an HTTP request calls `await ThrottleAsync(ct)` immediately before
`_httpClient.SendAsync(...)`. This pattern serialises all outbound requests regardless of
any concurrency in the Chronicle host.

### User Guidance

The settings form displays a note reminding users that fanedit.org is a community site and
that the rate limit exists to protect it. Users are strongly discouraged from setting the
delay lower than the default. The floor of 1,000 ms is enforced in code and cannot be
bypassed through configuration.

---

## Data Model

### ExternalId Format

```
fanedit:{ifdb-id}
```

Where `{ifdb-id}` is the numeric or slug-based identifier visible in the IFDB entry URL,
e.g. `fanedit:2847` or `fanedit:star-wars-the-rinzler-cut`. The slug form is preferred
when available because it is stable and human-readable; the numeric form is used when a
slug cannot be extracted.

This value is stored in `media_external_ids` with `Source = "fanedit"`.

### MediaMetadata Mapping

The `MediaMetadata` object returned by `GetByIdAsync` is populated as completely as
possible. Fields that have a direct generic mapping are set on the top-level properties;
everything else is packed into `ExtendedData`.

| IFDB field | `MediaMetadata` property | Notes |
|---|---|---|
| Fanedit title | `Title` | The fan edit's own name |
| Editor's description / overview | `Overview` | Full description text |
| Fanedit release year | `Year` | Year the edit was published |
| Fanedit runtime | `RuntimeMinutes` | Duration of the edit in minutes |
| Primary cover / poster image URL | `PosterUrl` | Stored as URL; never downloaded |
| Additional screenshots / images | `AdditionalImages` | See image handling below |
| Genre tags | `Genres` | From IFDB genre taxonomy |
| IFDB community rating | `Rating` | Normalised to 0–10 scale |
| Community/folksonomy tags | `Tags` | Any additional tag fields |
| All other fields | `ExtendedData` | Serialised as JSON; see below |

### ExtendedData Schema

All fanedit-specific metadata that has no generic `MediaMetadata` counterpart is stored in
`ExtendedData` as a `JsonElement`. This guarantees lossless ingestion – nothing is
discarded. The Chronicle frontend renders `ExtendedData` fields as a key-value grid in the
plugin's metadata box.

```jsonc
{
  // Source material
  "originalTitle":      "Star Wars: A New Hope",
  "originalYear":       1977,
  "originalImdbId":     "tt0076759",

  // Editor
  "editorUsername":     "DigModiFicaTion",
  "editorProfileUrl":   "https://www.fanedit.org/author/digmodification/",

  // Classification
  "fanEditType":        "Custom",
  "ifdbCategories":     ["Action", "Sci-Fi"],

  // Technical specifications
  "techSpecs": {
    "videoCodec":       "H.264",
    "audioCodec":       "AC3 5.1",
    "resolution":       "1080p",
    "aspectRatio":      "2.39:1",
    "containerFormat":  "MKV",
    "fileSizeGb":       null
  },

  // Edit details
  "changesList":        [
    "Removed all Jar Jar scenes",
    "Restored theatrical colour grading"
  ],
  "numberOfCuts":       42,
  "numberOfAdditions":  3,

  // Reception
  "ifdbRatingRaw":      "8.7",
  "ifdbRatingCount":    114,
  "ifdbAwards":         ["IFDB Award 2021 – Best Drama Edit"],

  // Publishing
  "ifdbId":             "2847",
  "ifdbUrl":            "https://www.fanedit.org/star-wars-the-rinzler-cut/",
  "ifdbPublishedDate":  "2021-03-15",

  // Distribution links
  "distributionLinks":  ["https://forum.fanedits.org/..."]
}
```

### Image Handling

Images are **stored as URLs only**. Chronicle never downloads or re-hosts images from
fanedit.org; it links to them directly.

| Image role | Chronicle field | Notes |
|---|---|---|
| Primary cover / poster | `MediaMetadata.PosterUrl` | First cover image on the IFDB entry |
| Additional screenshots | `MediaMetadata.AdditionalImages` | All other `<img>` elements within the edit's gallery section |

Each `AdditionalImage` entry:

```csharp
new AdditionalImage
{
    Url          = absoluteImageUrl,
    Type         = "Screenshot",
    ThumbnailUrl = thumbnailUrl
}
```

---

## Scraping Approach

### Page Structure

fanedit.org is a WordPress-based site. The parser must be defensive: any field that cannot
be found on the page is silently skipped rather than throwing.

#### Search

```
GET https://www.fanedit.org/ifdb/?s={url-encoded-query}&post_type=fanedit
```

For each result, extract: title, URL/slug, thumbnail URL, excerpt, year. Return as a
`IReadOnlyList<ScoredCandidate>` list scored as follows:

| Signal | Score |
|--------|-------|
| Exact title match (normalised, lower) | +40 |
| Fuzzy title match (Levenshtein ≤ 20%) | +20 |
| Year exact match | +20 |
| Year ±1 | +10 |
| Year mismatch > 1 | −10 |
| Source material title in search query | +10 |

Default acceptance threshold: **50**. If no candidate scores ≥ 50 after all AltTitles
are tried, the result is `NotFound`.

#### GetByIdAsync — Supported Input Formats

| Input | Resolution |
|-------|-----------|
| `https://www.fanedit.org/{slug}/` | Fetch detail page directly |
| `fanedit:{slug}` | Prepend base URL → fetch detail page |
| `fanedit:{integer}` | Treat integer as IFDB numeric ID → try `/ifdb/{id}/` |
| Bare integer string | Try as numeric IFDB ID |
| Bare slug string | Prepend `https://www.fanedit.org/` → fetch detail page |

#### Detail Page

```
GET https://www.fanedit.org/{ifdb-slug}/
```

Extraction priority (highest to lowest confidence):

1. **Structured meta tags** – `og:title`, `og:description`, `og:image`
2. **Schema.org JSON-LD** – `name`, `description`, `datePublished`, `author`
3. **Definition list / key-value pairs** – "Editor:", "Runtime:", "Video:", "Audio:", "Type:"
4. **Free-text body** – "Changes" section and "Editor's Notes"
5. **Gallery section** – all `<img>` elements collected as `AdditionalImages`

#### Fix Match Input Handling

- Full fanedit.org URL → call `GetByIdAsync` directly
- Bare numeric ID → call `GetByIdAsync` directly
- Otherwise → title search query

---

## Settings Schema

| Key | Label | Type | Required | Default | Notes |
|---|---|---|---|---|---|
| `username` | fanedit.org Username | Text | Yes | – | Registered fanedit.org account username |
| `password` | fanedit.org Password | Password | Yes | – | Stored encrypted; never logged |
| `request_delay_ms` | Request Delay (ms) | Number | No | `1000` | Floor: 1000. Be kind to the server. |
| `user_agent` | User-Agent String | Text | No | See below | Override HTTP User-Agent |

**Default User-Agent:**
```
Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36
```

---

## manifest.json

```json
{
  "plugin_id":             "chronicle.plugin.fanedit",
  "name":                  "FanEdit (IFDB)",
  "version":               "1.0.0",
  "author":                "Chronicle Contributors",
  "description":           "Fetches fanedit metadata from the Internet Fan Edit Database (fanedit.org). Requires a registered fanedit.org account. Please use responsibly – minimum 1-second delay between requests is enforced.",
  "min_chronicle_version": "0.1.0",
  "entry_type":            "Chronicle.Plugin.FanEdit.FanEditMetadataProvider",
  "iconUrl":               "https://www.fanedit.org/favicon.ico",
  "brandColorLight":       "#8B1A1A",
  "brandColorDark":        "#C0392B",
  "fixMatchHint":          "Enter a fanedit.org URL or an IFDB entry title",
  "background_tasks": [
    {
      "task_id":         "fetch-missing-metadata",
      "display_name":    "Fetch Missing Metadata",
      "description":     "Looks up IFDB metadata for newly imported fan edits that don't have it yet.",
      "default_cron":    "0 5 * * *",
      "default_enabled": true
    },
    {
      "task_id":         "resync-all-metadata",
      "display_name":    "Re-sync All Metadata",
      "description":     "Re-fetches IFDB metadata for all fan edits to pick up updated descriptions, ratings, and images.",
      "default_cron":    "0 4 * * 0",
      "default_enabled": false
    }
  ]
}
```

---

## Background Tasks

| Task | Schedule | Purpose |
|---|---|---|
| `fetch-missing-metadata` | Daily at 5:00 UTC | Enriches newly imported fanedit items |
| `resync-all-metadata` | Weekly Sunday 4:00 UTC | Re-fetches all matched entries (disabled by default) |

---

## Error Handling

| Condition | Behaviour |
|---|---|
| Login failure | `HealthCheckAsync` returns `false`; search/fetch throw with descriptive message |
| Session expired mid-run | Re-authenticate once, retry. If re-auth fails, throw. |
| HTTP 404 on detail page | Return `null` from `GetByIdAsync` |
| HTTP 429 / 503 | Back off `request_delay_ms * 3`, retry once |
| HTML field not found | Log warning, set field to `null`, continue parsing |
| Network timeout | 30 s default; rethrow as `HttpRequestException` with context |
| `CancellationToken` cancelled | Propagate immediately |

---

## Repository Structure

```
Chronicle.Plugin.FanEdit/
├── Chronicle.Plugin.FanEdit.csproj
├── README.md
├── manifest.json
├── FanEditMetadataProvider.cs
├── FanEditAuthService.cs
├── FanEditScraper.cs
├── FanEditRateLimiter.cs
└── Models/
    ├── FanEditEntry.cs
    ├── FanEditSearchResult.cs
    └── FanEditTechSpecs.cs
```

---

## Building & Packaging

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>Chronicle.Plugin.FanEdit</AssemblyName>
    <RootNamespace>Chronicle.Plugin.FanEdit</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <!-- Local path reference during development.
         Replace with a NuGet package reference once Chronicle.Plugins is published. -->
    <ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                      Private="false"
                      ExcludeAssets="runtime" />
    <!-- Chronicle.Core is a transitive dep of Chronicle.Plugins. Without this explicit
         Private="false" reference, MSBuild copies Chronicle.Core.dll into the plugin
         output dir and the deps.json lists it — causing PluginLoadContext to load the
         plugin-dir copy instead of falling through to the host's (current) version. -->
    <ProjectReference Include="..\Chronicle\src\Chronicle.Core\Chronicle.Core.csproj"
                      Private="false"
                      ExcludeAssets="runtime" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="HtmlAgilityPack" Version="1.11.*" />
  </ItemGroup>
  <ItemGroup>
    <None Update="manifest.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

---

## Branding Reference

| Plugin | Light | Dark |
|---|---|---|
| TMDB | `#01B4E4` | `#0d9ec9` |
| MusicBrainz | `#BA478F` | `#CF6BAA` |
| **FanEdit (IFDB)** | **`#8B1A1A`** | **`#C0392B`** |

---

## Important Notes on Use

1. **Personal use only.** Not appropriate for multi-user deployments.
2. **Respect fanedit.org.** Do not reduce delay below 1,000 ms or run resync more than weekly.
3. **No redistribution of scraped data.** Metadata is for personal reference only.
4. **Session security.** Password stored encrypted; never written to logs.

---

## License

MIT – see [LICENSE](LICENSE).

---

*Chronicle.Plugin.FanEdit is an independent community plugin and is not affiliated with, endorsed by, or officially supported by fanedit.org or any of its staff.*
