# Movies Remastered (MRDb) Plugin — Design

**Date:** 2026-07-25
**Status:** Proposed

---

## Overview

`Chronicle.Plugin.MoviesRemastered` — a new metadata provider that scrapes
[moviesremastered.com](https://www.moviesremastered.com/) ("MRDb" — the Movies Remastered
Database) for the **existing** `fanedits` media type. This mirrors
`Chronicle.Plugin.FanEdit` (the fanedit.org/IFDB provider, see
`docs/plans/2026-04-13-fanedit-plugin-design.md`) as closely as the two sites' data allow.

Unlike the FanEdit plugin, this is **purely additive** — no Chronicle core changes are
needed. See below.

---

## Why this doesn't touch Chronicle core

Confirmed by reading `PluginRegistry`, `MetadataEnrichmentService`, and
`MediaItemEnrichment`: the plugin architecture already supports multiple concurrent
`IMetadataProvider`s declaring the same media type. It's additive, not exclusive —

- `MetadataEnrichmentService.EnrichItemAsync` / `EnrichPendingAsync` loop over **every**
  registered provider that supports an item's type and create one `MediaItemEnrichment` row
  per `(item, plugin)` pair — a failure in one plugin doesn't block another.
- The Enrichment Status table on the Background Tasks page already shows one row per
  installed plugin with independent counts — a second `fanedits` provider just adds a row.
- Conflicting field values across providers are already reconciled generically, per field,
  via **Settings → Metadata Assignment** (`MetadataAssignmentPage.tsx` /
  `MetadataResolutionService.ResolveAsync`) — a drag-to-reorder priority list where "the
  first plugin in each row is the primary source; the rest are fallbacks." This already
  exists for exactly this situation and needs no plugin-specific code.
- The `fanedits` media type, and the generic `schedulable` / `run_confirmation` manifest
  fields, already merged as part of the FanEdit plugin work.

So this plugin is a new, independent repo (`W:\Scripts\Chronicle.Plugin.MoviesRemastered`,
same pattern as TMDB/MusicBrainz/FanEdit) with zero changes to `W:\Scripts\Chronicle` itself.

---

## Site profile: moviesremastered.com (MRDb)

Confirmed by browsing the live site:

- **No login required.** Unlike fanedit.org, search results and full detail pages —
  including synopsis, change list, intentions, and ratings — are all visible unauthenticated.
  No `AuthService` equivalent is needed.
- **Numeric IDs, no slugs**: canonical detail URL is `movieinfo.php?id={n}`.
- **Search**: `GET /searchresults.php?searchtype={field}&searchterm={query}` (plus optional
  `genre=`, `franchise=`, `certificate=`, `award=`, `language=`, `fanedittype=` filters).
  `searchtype` accepts (from the site's own dropdown): `Title`, `OriginalTitle`, `Genre`,
  `Franchise`, `Faneditor`, `User`, `FanEditType`, `Certificate`, `Award`, `Language`,
  `WishlistIdeas`, `MRDbCharts`, `Reviews`.
- **Detail page fields confirmed present**: Faneditor, Fanedit Type, Fanedit Release Date,
  Fanedit Runtime, Time Cut, Time Added, Franchise, Genre (multi-value), Original Title,
  Original Release Date, Original Runtime, Certificate, Source, Resolution, Sound Mix,
  Language, Subtitles, Synopsis, Intentions, Change List, MRDb Rating (or "No votes"), Views,
  Reviews count, Favorites count.
- **Legal/terms splash — confirmed JS-only.** A plain `curl` GET of `movieinfo.php?id=12179`
  (no cookies, no JS) returned `200` with the full rendered page (title, synopsis, all
  detail fields) — the "LEGAL DISPATCH" splash never appears server-side. It's a client-side
  animation only; a plain `HttpClient` scraper is unaffected.
- Same "metadata archive, not a content host" posture as fanedit.org, which is the same
  legal footing the FanEdit plugin already relies on.

**Raw HTML confirmed** (via direct `curl` fetch of a detail and a search-results page —
see Implementation, Task 1 for the full findings this design is based on):

- OpenGraph tags present: `og:title`, `og:description`, `og:image`, `og:url`, `og:type`,
  `og:site_name`, `og:locale`.
- Schema.org JSON-LD present, `@type: "Movie"`: `name`, `description`, `image`, `url`,
  `keywords`. No `datePublished`/`genre`/`aggregateRating` in the LD+JSON — those are
  detail-page-only fields (see below).
- Real favicon: `https://www.moviesremastered.com/favicon.ico` (confirmed via
  `<link rel="icon">` in the page `<head>`) — no need for a synthesized icon like FanEdit's
  inline base64 SVG.
- Detail-page fields are **not** in a `<dl>`/definition-list structure (unlike fanedit.org's
  JReviews markup) — they're flat `<B>Label: </B>value<BR>` pairs inside a plain `<div
  class=column>`, e.g.:
  ```html
  <B>Faneditor: </B><A HREF=Spartan47>Spartan47</A>...<BR>
  <B>Fanedit Type: </B>TV-to-Movie<BR>
  <B>Fanedit Release Date: </B>25th July 2026<BR>
  <B>Fanedit Runtime: </B>3h:38m:0s<BR>
  <B>Franchise: </B><A HREF=searchresults.php?searchtype=Franchise&franchise=Game+of+Thrones>Game of Thrones</A><BR>
  <B>Genre: </B><A HREF=...&genre=Adventure>Adventure</A> • <A HREF=...&genre=Drama>Drama</A><BR>
  <B>Certificate: </B>18<BR><B>Source: </B>4K<BR><B>Resolution: </B>4k<BR>
  <B>Sound Mix: </B>5.1. Channels<BR><B>Language: </B>English<BR><B>Subtitles: </B>English • Spanish<BR>
  ```
  Runtime fields render as `{h}h:{m}m:{s}s` (e.g. `3h:38m:0s`), not plain minutes.
- Synopsis / Intentions / Change List are each an `<H3>Label:</H3>` followed by free text in
  a sibling `<div style="white-space:pre-wrap">`, separated by `<HR>`:
  ```html
  <DIV ...><H3 style="color:red;">Synopsis:</H3>As the Seven Kingdoms are consumed...
  <BR><BR></DIV><HR><DIV ...><H3 style="color:red;">Intentions:</H3>To combine Jon and Bran's...
  <BR><BR></DIV><HR><DIV ...><H3 style="color:red;">Change List:</H3>Combined Jon Snow's storyline...
  ```
- The ratings/stats block uses a cleaner `div.stats-item` structure:
  ```html
  <div class="stats-item"><B>MRDb Rating</B><br><i class="fa-solid fa-star"></i> No votes</div>
  <div class="stats-item"><B>Views</B><br><IMG SRC="views icon.png">&nbsp95</div>
  <div class="stats-item"><B>Reviews</B><br><B id=reviewcount>0</B></div>
  <div class="stats-item"><B>Favorite</B><br>...<SPAN ID=favcnt>0</SPAN></div>
  ```
- Poster image: `og:image` gives a stable, cache-param-free URL
  (`https://moviesremastered.com/images/{id}-posterart.jpeg`) — prefer this over the
  in-body `<IMG>` variants, which carry a `?cb=` cache-busting query string and, for the
  medium-size version, an `/images/mi/` path segment.
- Search-results-page cards use a similar flat `<B>Label:</B> <span>value</span><BR>`
  pattern inside `div.result-card`, with the title as `<B style='font-size:1.2em;'><A
  HREF=/movieinfo.php?id={n}>{title}</A></B>`.

---

## Project structure

```
Chronicle.Plugin.MoviesRemastered/
├── Chronicle.Plugin.MoviesRemastered.csproj
├── README.md
├── manifest.json
├── MoviesRemasteredMetadataProvider.cs   IMetadataProvider entry point
├── MoviesRemasteredScraper.cs            HtmlAgilityPack HTML parsing
├── MoviesRemasteredRateLimiter.cs        SemaphoreSlim + Stopwatch throttle
└── Models/
    ├── MoviesRemasteredEntry.cs
    ├── MoviesRemasteredSearchResult.cs
    └── MoviesRemasteredTechSpecs.cs
```

No `AuthService` file — no login flow exists for this site.

### .csproj

Same shape as `Chronicle.Plugin.FanEdit.csproj`: `net9.0`, `HtmlAgilityPack` package
reference, `ProjectReference`s to `Chronicle.Plugins` and `Chronicle.Core`
(`Private=false`, `ExcludeAssets=runtime`), `manifest.json` copied to output.

### manifest.json

```json
{
  "plugin_id":             "chronicle.plugin.moviesremastered",
  "name":                  "Movies Remastered (MRDb)",
  "version":               "1.0.0",
  "author":                "Chronicle Contributors",
  "description":           "Fetches fan edit metadata from the Movies Remastered Database (moviesremastered.com / MRDb), a community fanedit archive. No account required. Please use responsibly — a minimum 1-second delay between requests is enforced.",
  "min_chronicle_version": "0.1.0",
  "entry_type":            "Chronicle.Plugin.MoviesRemastered.MoviesRemasteredMetadataProvider",
  "iconUrl":               "TBD — sample the real favicon during implementation",
  "brandColorLight":       "TBD — sample real site colors during implementation",
  "brandColorDark":        "TBD — sample real site colors during implementation",
  "fixMatchHint":          "Enter a moviesremastered.com URL (e.g. https://www.moviesremastered.com/movieinfo.php?id=12179) or a bare MRDb numeric ID",
  "background_tasks": [
    {
      "task_id":         "fetch-missing-metadata",
      "display_name":    "Fetch Missing Metadata",
      "description":     "Looks up MRDb metadata for fan edits that don't have it yet.",
      "default_cron":    null,
      "default_enabled": false,
      "schedulable":     false,
      "run_confirmation": {
        "title":   "Fetch MRDb metadata?",
        "message": "Movies Remastered is a small community site. This task makes one HTTP request per fan edit with a minimum 1-second delay between each — on a large library this will take a long time. Please run this sparingly, not more than a few times per week."
      }
    },
    {
      "task_id":         "resync-all-metadata",
      "display_name":    "Re-sync All Metadata",
      "description":     "Re-fetches MRDb metadata for all fan edits to pick up updated descriptions, ratings, and images.",
      "default_cron":    null,
      "default_enabled": false,
      "schedulable":     false,
      "run_confirmation": {
        "title":   "Re-sync all MRDb metadata?",
        "message": "This will re-fetch MRDb metadata for every fan edit in your library. Movies Remastered is a small community site — each request has a minimum 1-second delay. On a large library this will take a very long time. Please use this sparingly."
      }
    }
  ]
}
```

### Settings schema

| Key | Label | Type | Required | Default | Notes |
|---|---|---|---|---|---|
| `request_delay_ms` | Request Delay (ms) | Number | No | `1000` | Floor: 1000 ms enforced in code (courtesy default — MRDb doesn't publish a stated rate limit like fanedit.org does, but the same floor is kept for consistency and to avoid hammering volunteer infrastructure) |
| `user_agent` | User-Agent String | Text | No | Chrome UA | Override HTTP User-Agent |

No username/password fields — MRDb requires no account for search or detail access.

### GetSupportedMediaTypes

Returns `"fanedits"` — the same type `Chronicle.Plugin.FanEdit` declares. Confirmed
compatible with the current architecture (see "Why this doesn't touch Chronicle core"
above): multiple providers per type is the expected, supported case, not a special one.

### Rate limiting

Identical pattern to `FanEditRateLimiter`: `SemaphoreSlim(1,1)` + `Stopwatch`, hard-coded
1,000 ms floor that configuration cannot lower.

### SearchAsync

`GET /searchresults.php?searchtype=Title&searchterm={url-encoded-query}`

Tries all `context.AltTitles` plus `context.Name`. Same scoring approach as FanEdit:

| Signal | Score |
|---|---|
| Exact title match (normalised, lower-case) | +40 |
| Fuzzy title match (Levenshtein ≤ 20%) | +20 |
| Year exact match | +20 |
| Year ±1 | +10 |
| Year mismatch > 1 | −10 |
| Source material title in query | +10 |

Acceptance threshold: **50**. Returns up to 10 candidates sorted by descending score.

### GetByIdAsync

| Input | Resolution |
|---|---|
| `https://www.moviesremastered.com/movieinfo.php?id={n}` | Fetch detail page directly |
| `mrdb:{n}` | Prepend base URL |
| Bare integer | Try as numeric MRDb ID |

No slug form — MRDb only uses numeric IDs, unlike fanedit.org.

### Detail page extraction

Confirmed via raw HTML (see "Raw HTML confirmed" above). Extraction priority:

1. `og:title` / JSON-LD `name` for `Title` (JSON-LD preferred — cleaner, no site-name suffix
   that `og:title`/`<title>` carry, e.g. `"Snow: Part I | MRDb Fanedits"`).
2. JSON-LD `description` / `og:description` for `Overview` as a fallback, but the `<H3>
   Synopsis:</H3>` sibling-div text (see below) is preferred when present since it's the
   full, unclipped synopsis (`og:description`/JSON-LD `description` may be present but
   are the same full text here, unlike FanEdit where OG description was often clipped).
3. `og:image` for `PosterUrl` — stable URL, no cache-bust param (see above).
4. Walk `<B>Label: </B>` nodes and take the following sibling text/anchor content up to the
   next `<B>` or `<BR>` — this is the only viable strategy given the flat, non-semantic
   markup (no `class` hooks on values, unlike FanEdit's `jrFieldRow`/`jrFieldLabel`/
   `jrFieldValue` CSS classes). Labels to match: `Faneditor:`, `Fanedit Type:`, `Fanedit
   Release Date:`, `Fanedit Runtime:`, `Time Cut:`, `Time Added:`, `Franchise:`, `Genre:`,
   `Original Title:`, `Original Release Date:`, `Original Runtime:`, `Certificate:`,
   `Source:`, `Resolution:`, `Sound Mix:`, `Language:`, `Subtitles:`.
5. `Synopsis:` / `Intentions:` / `Change List:` — find the `<H3>` whose text matches, take
   the free text of its parent `<div>` after removing the `<H3>` itself.
6. `div.stats-item` blocks for `MRDb Rating` (→ `null` when text is `"No votes"`), `Views`,
   `Reviews`, `Favorite`.

Runtime strings like `3h:38m:0s` need parsing to total minutes (`3*60 + 38 = 218`) — matches
the `218` minutes seen on the search-results card for the same item, confirming the
search-results page pre-converts to plain minutes while the detail page does not.

Any field not found on the page is silently set to `null` — never throws (same contract as
FanEdit).

### MediaMetadata mapping

| MRDb field | `MediaMetadata` property |
|---|---|
| Fanedit Title | `Title` |
| Synopsis | `Overview` |
| Fanedit Release Date (year) | `Year` |
| Fanedit Runtime | `RuntimeMinutes` |
| Cover image | `PosterUrl` |
| Genre | `Genres` |
| MRDb Rating | `Rating` (0–10, `null` if "No votes") |
| Franchise, Fanedit Type | `Tags` |
| Everything else | `ExtendedData` (JSON) |

`ExtendedData` captures: original title/release date/runtime, faneditor username + profile
URL, time cut, time added, certificate, source, resolution, sound mix, language, subtitles,
intentions, change list, views, favorites count, reviews count, MRDb numeric ID.

### External ID format

`mrdb:{id}`. Stored in `media_external_ids` with `Source = "moviesremastered"`.

### Error handling

| Condition | Behaviour |
|---|---|
| HTTP 404 on detail page | Return `null` from `GetByIdAsync` |
| HTTP 429 / 503 | Back off `delay * 3`, retry once |
| HTML field missing | Log warning, field = `null`, continue |
| Network timeout | 30 s; rethrow as `HttpRequestException` with context |
| `CancellationToken` | Propagate immediately |

### Enrichment Status integration

Automatic — standard `IMetadataProvider` behavior. Appears as its own independent row in
the Enrichment Status table once installed, alongside (not merged with)
`Chronicle.Plugin.FanEdit`'s row.

### Coexistence with Chronicle.Plugin.FanEdit

Once both plugins are installed and enabled, `fanedits`-typed items get two independent
`MediaItemEnrichment` rows — one per plugin. Any field populated by both is reconciled by
the user, generically, via **Settings → Metadata Assignment** — no plugin-specific merge
logic is written or needed.

### Branding

- `iconUrl`: `https://www.moviesremastered.com/favicon.ico` (confirmed real, unlike
  FanEdit's synthesized inline SVG — fanedit.org's own favicon presumably wasn't suitable).
- `brandColorLight` / `brandColorDark`: the site's own stylesheet is almost entirely
  black/white/gray (confirmed — `body { color: white; background-color: #000; }`); the
  only accent colors used on the page are the rating-star gold (`#ebcd0a`) and the
  section-header red (`H3 { color: red }`). Proposing `brandColorLight: "#C0392B"` /
  `brandColorDark: "#E74C3C"` (red family, distinct from FanEdit's dark-red pair so the two
  plugins are visually distinguishable in the Enrichment Status table) — confirm against
  the rendered site logo before finalizing, since a stylesheet fetch returned an empty file
  and the true brand mark may live only in the logo image
  (`/users/images/logonew.png`).

See implementation Task 8 for finalizing this against the live logo image.

---

## Implementation sequence

See `docs/plans/2026-07-25-moviesremastered-plugin-impl.md` for the full task-by-task plan.
Summary:

1. Project scaffold — `Chronicle.Plugin.MoviesRemastered.csproj`, `manifest.json` (initial
   branding values above), `Models/` stubs
2. `MoviesRemasteredRateLimiter` (same shape as `FanEditRateLimiter`)
3. `MoviesRemasteredScraper` — search-results parsing
4. `MoviesRemasteredScraper` — detail-page parsing (`<B>Label:</B>` walk, `<H3>` sections,
   `div.stats-item` ratings)
5. `MoviesRemasteredMetadataProvider` — identity/capabilities/settings/`Configure`
6. `MoviesRemasteredMetadataProvider` — `SearchAsync` + scoring
7. `MoviesRemasteredMetadataProvider` — `GetByIdAsync` + `MapToMetadata`
8. Finalize `manifest.json` branding against the live logo; `README.md`
9. Full build + test pass across the new repo — no Chronicle core integration tests needed
   since no core endpoints change
