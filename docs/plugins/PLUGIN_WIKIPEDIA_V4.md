# Chronicle Plugin Specification: Wikipedia (V4)

Supersedes `PLUGIN_WIKIPEDIA_V3.md`, which only covered the REST summary endpoint (lead
paragraph, one image) and used a `MediaTypeName: "*"` wildcard that Chronicle does not actually
support (every `GetSupportedMediaTypes()` call site matches literal type-name strings from the
DB — confirmed against `MetadataEnrichmentService.cs`, `FileScanService.cs`, `MediaService.cs`,
etc.). This version adds full-article section/heading extraction, article-wide image collection,
a numeric scoring model built around Wikidata short descriptions, and a well-mannered rate-limit
design in the house style (`Chronicle.Plugin.MusicBrainz` / `Chronicle.Plugin.FanEdit`). It also
adds a `people` type (Section 12) so cast/crew — Tom Cruise, Anson Mount, a film's director,
etc. — resolve to their own Wikipedia biography rather than being left as flat strings scraped
out of a movie's article.

**Update:** the people-section design has since landed —
`docs/plans/2026-08-28-people-section-design.md`. `people`'s `DisplayName` is no longer empty
(Section 12 below reflects this): Wikipedia is now the canonical registrant of the `people` media
type via the normal `PluginHostService.SyncMediaTypesFromPluginsAsync` upsert path, the same
mechanism every other plugin-declared type already goes through. `HierarchyLevels = 1`,
`HierarchyLabels = null`, `InteractionVerb = "viewed"`, `ProgressUnit = "percent"` (inert —
people are catalog-wide, not tracked through the watch/library/interaction system at all, per
that design's Section 1.4), `SupportsCollections = false`.

Wikipedia is a **broad, low-priority fallback provider** for every media type — it is never the
authority for a type (it declares no `DisplayName`, so it never creates or owns a row in
`media_types`), and its `DefaultPriority` is deliberately low so it only wins a field in the
Metadata Assignment system when an admin explicitly ranks it above the type-specific providers
(TMDB, MusicBrainz, Hardcover, etc.) or when nothing else has an answer.

---

## Section 1 — Service Overview

**Service Name:** Wikipedia
**Website:** https://wikipedia.org

Wikipedia is a free, multilingual, crowd-edited encyclopedia. Coverage is extremely broad but
uneven — flagship movies, major albums, and notable games have detailed articles; individual
songs, TV episodes, and minor tracks usually do not (this is expected and handled by scoring,
not by excluding those hierarchy levels — see Section 11). Coverage of people is the inverse:
almost every actor, director, musician, or other individual notable enough to appear in cast/crew
credits has a dedicated biography article, frequently a well-developed one (Early life, Career,
Personal life sections, a photo) — `people` is, if anything, the type this plugin will match most
reliably.

**Chronicle media types this plugin targets:** all of them, genuinely, via a declared list (see
Section 12) rather than a wildcard — Chronicle's plugin contract has no wildcard mechanism.
Critically, **the plugin contains zero per-type branching logic.** `SearchAsync`/`GetByIdAsync`
treat every media type identically: build a text query from `Name` (+ `ParentName` at hierarchy
levels 1–2), fetch an article, extract sections/images generically. Supporting a brand-new
Chronicle media type later is a one-line addition to the `MediaTypeSupport[]` array, not new
code — this is the closest this interface allows to "DB-configurable media type support" given
`GetSupportedMediaTypes()` is a synchronous, DB-free method on every `IMetadataProvider`.

**Auth:** None required for read access.
**Cost:** Free, no published quota for reasonable use (see Section 10).
**API docs:** https://www.mediawiki.org/wiki/API:Main_page and
https://www.mediawiki.org/wiki/API:REST_API — both are read directly in this spec, not deferred.

---

## Section 2 — Authentication & Credential Acquisition

No API key, OAuth, or registration needed. The only requirement is the
[Wikimedia User-Agent policy](https://meta.wikimedia.org/wiki/User-Agent_policy): every request
must carry a descriptive `User-Agent` identifying the client and a way to contact its operator.
Unidentified/generic UAs (default `HttpClient` UA, browser-spoofed UAs) are the primary trigger
for Wikimedia rate-limiting a client harder or blocking it outright.

Per your decision, **the contact identifier is a required plugin setting**, not hardcoded —
each self-hoster supplies their own (see Section 3). The header is built once in the client
constructor:

```
User-Agent: Chronicle/{ChronicleVersion} (+{contact_info}) HttpClient/{dotnet version}
```

No token expiry, no ToS click-through beyond the standard Wikimedia terms of use (CC BY-SA /
GFDL content licensing — relevant to Section 13's copyright note, not to auth).

---

## Section 3 — Plugin Settings Schema

| Key | Label | Description | Type | Required | Default |
|---|---|---|---|---|---|
| `language` | Language | Wikipedia language subdomain to search, e.g. `en`, `de`, `fr`, `ja`. One language per plugin instance — Chronicle doesn't support multi-value settings that fan out into multiple searches. | Text | Yes | `"en"` |
| `contact_info` | Contact Info (for User-Agent) | A URL or email identifying this Chronicle instance's operator, sent in every request's `User-Agent` header per Wikimedia's User-Agent policy. Use your Chronicle instance's repo/homepage URL, or an email you monitor. Required — Wikimedia may throttle or block requests with no way to contact the operator. | Text | Yes | *(none)* |
| `min_request_interval_ms` | Minimum Request Interval (ms) | Floor on time between outbound requests to Wikipedia. 100ms (10 req/s) is already conservative relative to Wikimedia's guidance; raise it to be gentler on a shared/low-resource Wikimedia mirror. Cannot be set below 50ms. | Number | No | `"100"` |
| `max_images` | Max Images per Article | Upper bound on how many images from one article are stored as `AdditionalImages`. Prevents very heavily-illustrated articles (discographies, "List of..." pages) from bloating `metadata_json`. | Number | No | `"20"` |

`SettingType.Password` is intentionally not used for `contact_info` — it's not a secret, and
masking it would just make misconfiguration harder to spot in the settings UI.

---

## Section 4 — Manifest Values

`manifest.json`:

```json
{
  "plugin_id":             "chronicle.plugin.wikipedia",
  "name":                  "Wikipedia",
  "version":               "1.0.0",
  "author":                "Chronicle Contributors",
  "description":           "Broad fallback summaries, article sections, and images from Wikipedia for any media type.",
  "min_chronicle_version": "0.7.0",
  "entry_type":            "Chronicle.Plugin.Wikipedia.WikipediaMetadataProvider",
  "iconUrl":               "https://en.wikipedia.org/static/apple-touch/wikipedia.png",
  "brandColorLight":       "#000000",
  "brandColorDark":        "#F0F0F0",
  "fixMatchHint":          "Paste a Wikipedia URL, or type lang:Page_Title (e.g. en:The_Batman_(film))",
  "background_tasks": [
    {
      "task_id":         "fetch-missing-metadata",
      "display_name":    "Fetch Missing Wikipedia Matches",
      "description":     "Searches Wikipedia for items that don't have a match yet.",
      "default_cron":    "0 5 * * *",
      "default_enabled": true
    },
    {
      "task_id":         "resync-all-metadata",
      "display_name":    "Re-sync All Wikipedia Articles",
      "description":     "Re-fetches article text/sections/images for all items already matched to Wikipedia. Articles change often — run this more frequently than most providers' resync task if you want fresh content.",
      "default_cron":    "0 6 * * 0",
      "default_enabled": false
    }
  ]
}
```

`brandColorLight`/`brandColorDark` are corrected from V3, which had them backwards (white
accent on a light card, black accent on a dark card — both invisible). Wikipedia's mark is
effectively grayscale, so black-on-light / near-white-on-dark is the only pairing that's
visible in both themes.

---

## Section 5 — Search Endpoint Specification

**Media types covered:** all (Section 12). One endpoint shape for every type — the query text
is built generically from `MediaSearchContext`, never branched by `MediaTypeName`.

### Query construction

```
query = context.PreciseName ?? context.Name
if context.HierarchyLevel > 0 and context.ParentName is not null:
    query = $"{query} {context.ParentName}"      // disambiguates "Ozymandias" -> "Ozymandias Breaking Bad"
if context.HierarchyLevel == 2 and context.GrandparentName is not null:
    query = $"{query} {context.GrandparentName}" // track -> include artist too
```

If the first query returns zero candidates, retry once using `context.FilenameStem` (per
`AltTitles` fallback convention in `MediaSearchContext`'s own docs) before giving up — this is
the only retry `SearchAsync` performs; it does not walk the full `AltTitles` list to keep
request count bounded (well-mannered).

### Endpoint — combined search + description + thumbnail (one request)

Rather than a bare `list=search` call followed by per-result detail fetches, everything scoring
needs comes back in **one** request using a search-backed generator plus three `prop` modules:

- **HTTP Method:** `GET`
- **Base URL:** `https://{lang}.wikipedia.org/w/api.php`
- **Query parameters:**

| Name | Value | Notes |
|---|---|---|
| `action` | `query` | |
| `generator` | `search` | Use search results as the page set for the `prop` modules below. |
| `gsrsearch` | `{query}` | URL-encoded. |
| `gsrlimit` | `8` | Chronicle wants "all plausible candidates," not just the top hit; 8 keeps the response small. |
| `gsrnamespace` | `0` | Article namespace only — excludes Talk/Category/Template pages. |
| `prop` | `extracts\|pageimages\|pageprops\|pageterms` | Four modules combined in one call. |
| `exintro` | `1` | Extract only the lead section (cheap; full sections come later, only for the accepted candidate). |
| `explaintext` | `1` | Plain text, no HTML to strip at search time. |
| `exsentences` | `3` | Cap the lead extract used for scoring/display-preview. |
| `piprop` | `thumbnail` | |
| `pithumbsize` | `300` | Small — this is just for the search-result preview, not the final poster. |
| `ppprop` | `disambiguation\|wikibase_item` | Presence of `disambiguation` flags a disambig page (hard-reject candidate). |
| `wbptterms` | `description` | Wikidata short description, e.g. `"2022 American superhero film"` — the primary type-matching signal (Section 11). |
| `format` | `json` |  |
| `formatversion` | `2` | Flatter, less nested response shape. |

**Example request:**
```
GET https://en.wikipedia.org/w/api.php?action=query&generator=search&gsrsearch=The%20Batman%20film&gsrlimit=8&gsrnamespace=0&prop=extracts%7Cpageimages%7Cpageprops%7Cpageterms&exintro=1&explaintext=1&exsentences=3&piprop=thumbnail&pithumbsize=300&ppprop=disambiguation%7Cwikibase_item&wbptterms=description&format=json&formatversion=2
```

**Example response (real shape, two full elements shown):**
```json
{
  "batchcomplete": true,
  "query": {
    "pages": [
      {
        "pageid": 68312338,
        "ns": 0,
        "title": "The Batman (film)",
        "index": 1,
        "extract": "The Batman is a 2022 American superhero film based on the DC Comics character of the same name. Produced by DC Films, 6th & Idaho, and Dylan Clark Productions, and distributed by Warner Bros. Pictures, it is a reboot of the Batman film franchise.",
        "thumbnail": {
          "source": "https://upload.wikimedia.org/wikipedia/en/thumb/f/f7/The_Batman_%28film%29_poster.jpg/300px-The_Batman_%28film%29_poster.jpg",
          "width": 300,
          "height": 444
        },
        "pageimage": "The_Batman_(film)_poster.jpg",
        "pageprops": {
          "wikibase_item": "Q64768688"
        },
        "terms": {
          "description": ["2022 film by Matt Reeves"]
        }
      },
      {
        "pageid": 4335544,
        "ns": 0,
        "title": "Batman",
        "index": 2,
        "extract": "Batman is a superhero appearing in American comic books published by DC Comics. The character was created by artist Bob Kane and writer Bill Finger, and debuted in the 27th issue of the comic book Detective Comics on March 30, 1939.",
        "thumbnail": {
          "source": "https://upload.wikimedia.org/wikipedia/en/thumb/f/ff/Detective_Comics_27.jpg/300px-Detective_Comics_27.jpg",
          "width": 300,
          "height": 442
        },
        "pageimage": "Detective_Comics_27.jpg",
        "pageprops": {
          "wikibase_item": "Q2695156"
        },
        "terms": {
          "description": ["fictional superhero appearing in American comics published by DC Comics"]
        }
      }
    ]
  }
}
```

**Pagination:** none needed — `gsrlimit` caps results directly, no continuation token followed.
**Error responses:** MediaWiki's `action=query` does not HTTP-error on "no results"; a query
that matches nothing returns `{"batchcomplete": true}` with no `query.pages` key at all — treat
absence of `query.pages` as zero candidates, not an error. Malformed requests (e.g. missing
`gsrsearch`) return HTTP 200 with a top-level `"error"` object:
```json
{"error": {"code": "invalidparammix", "info": "..."}, "servedby": "..."}
```
Rate-limit and transient errors are covered in Section 10.

---

## Section 6 — Fetch-by-ID Endpoint Specification

`GetByIdAsync` makes **two** requests per accepted match (only once per item — the result is
cached in `metadata_json` after that, re-fetched only by the `resync-all-metadata` task):

### 6a. Poster + cross-refs — `action=query` (same shape as search, single title)

- **HTTP Method:** `GET`
- **URL:** `https://{lang}.wikipedia.org/w/api.php?action=query&titles={title}&prop=pageimages|pageprops|categories&piprop=original|thumbnail&pithumbsize=1000&ppprop=wikibase_item&cllimit=50&clshow=!hidden&format=json&formatversion=2`

Requesting `piprop=original` gets Wikipedia's own algorithmically-chosen "page image" (usually
the infobox image) at full resolution — this is more reliable than picking the first `<img>` out
of the article body, which can just as easily be a small flag icon or coordinate marker.
`categories` (filtered to non-hidden, `clshow=!hidden`) feeds the `Tags` field.

**Example response:**
```json
{
  "query": {
    "pages": [
      {
        "pageid": 68312338,
        "title": "The Batman (film)",
        "original": {
          "source": "https://upload.wikimedia.org/wikipedia/en/f/f7/The_Batman_%28film%29_poster.jpg",
          "width": 1000,
          "height": 1482
        },
        "thumbnail": {
          "source": "https://upload.wikimedia.org/wikipedia/en/thumb/f/f7/The_Batman_%28film%29_poster.jpg/1000px-The_Batman_%28film%29_poster.jpg",
          "width": 1000,
          "height": 1482
        },
        "pageprops": { "wikibase_item": "Q64768688" },
        "categories": [
          { "ns": 14, "title": "Category:2022 films" },
          { "ns": 14, "title": "Category:American superhero films" },
          { "ns": 14, "title": "Category:Films directed by Matt Reeves" }
        ]
      }
    ]
  }
}
```

### 6b. Full article body — REST API, Parsoid HTML

- **HTTP Method:** `GET`
- **URL:** `https://{lang}.wikipedia.org/api/rest_v1/page/html/{title}`
- **Headers:** `Accept: text/html; charset=utf-8; profile="https://www.mediawiki.org/wiki/Specs/HTML/2.8.0"` (optional but avoids a profile-negotiation redirect), plus the standard `User-Agent`.

This returns the **entire current article** as one Parsoid-rendered HTML document — a single
request regardless of article length, which is what makes full-article capture well-mannered
(no per-section round trips). Parsoid wraps each top-level heading's content in its own
`<section data-mw-section-id="N">` element, with `N=0` for the untitled lead section — this is
what makes section-splitting a straightforward DOM walk rather than a wikitext parser (see
Section 14 for the extraction algorithm).

**Example response (structure excerpt — real articles run tens of KB, only the shape matters
here):**
```html
<!DOCTYPE html>
<html>
<body>
<section data-mw-section-id="0">
  <p><b>The Batman</b> is a 2022 American superhero film based on the
  <a rel="mw:WikiLink" href="./DC_Comics" title="DC Comics">DC Comics</a>
  character of the same name...</p>
  <p>The film had its world premiere...</p>
</section>
<section data-mw-section-id="1">
  <h2 id="Plot">Plot</h2>
  <p>In his second year of fighting crime, Batman...</p>
</section>
<section data-mw-section-id="2">
  <h2 id="Cast">Cast</h2>
  <ul><li><a href="./Robert_Pattinson">Robert Pattinson</a> as Bruce Wayne / Batman</li></ul>
</section>
<section data-mw-section-id="9">
  <h2 id="References">References</h2>
  <div class="reflist"><ol class="references">...</ol></div>
</section>
</body>
</html>
```

**Error responses:**
- `404` with `{"type": ".../pagenotfound", "title": "Not found."}` — title doesn't exist as
  given. Retry once via `action=query&redirects=1&titles={title}&format=json` to resolve a
  redirect (e.g. a user pasted `The Batman` which redirects to `The Batman (film)`), then retry
  the REST call with the resolved title. If the second attempt also 404s, throw
  `NotFoundException`.
- `503`/`429` — see Section 10.

**Which response feeds which field:** see Section 7 and Section 14.

---

## Section 7 — Field Mapping Table

| MediaMetadata Field | API Response Path | Notes / Transformation |
|---|---|---|
| `ExternalId` | — | `"wikipedia:{lang}:{title}"`, title with spaces as underscores (Wikipedia's own convention). |
| `Source` | — | `"wikipedia"` |
| `Title` | `query.pages[].title` | Canonical title including disambiguator, e.g. `"The Batman (film)"`. |
| `Overview` | Section 0 of the parsed HTML body (Section 14), plain text | Not the `extract` field from search — that's capped at 3 sentences for the search preview only. The stored `Overview` is the full lead section. |
| `Year` | not available | Wikipedia doesn't expose a structured release-year field via these endpoints; a year could theoretically be regex-scraped from the lead sentence but that's unreliable enough to skip — leave `null` and let TMDB/MusicBrainz/etc. own this field. For the `people` type specifically, do **not** repurpose `Year` as birth year — that field means release/publish year everywhere else it's read, and overloading it would silently corrupt any cross-type display logic. Birth/death dates for people go in `ExtendedData` instead (see below). |
| `PosterUrl` | `query.pages[].original.source` (6a) | Falls back to `thumbnail.source` if `original` absent (very rare — means Wikipedia couldn't compute a full-size render). |
| `BackdropUrl` | not available | Wikipedia has no wide/backdrop-shaped image concept. Left `null`. |
| `RuntimeMinutes` | not available | `null`. |
| `Genres` | not available as a clean list | Wikipedia categories are noisy (production-year, nationality, awards, "films directed by X" — see `Tags` instead). `null`/empty. |
| `Cast` | not available as structured list | Cast appears in article prose/lists but extracting it reliably (vs. crew, vs. "voiced by" for animation) is out of scope — Wikipedia is not the authority for this field. Empty. |
| `Directors` | not available | Same reasoning. Empty. |
| `Rating` | not available | Wikipedia carries no numeric rating. `null`. |
| `Tags` | `query.pages[].categories[].title` (6a) | Strip the `Category:` prefix; drop maintenance/meta categories via the stoplist in Section 13 (`"CS1 ..."`, `"Articles with ..."`, `"Wikipedia articles ..."`, `"Use mdy dates"`, etc.); cap at 15 tags after filtering. |
| `AdditionalImages` | `<img>` elements across the parsed HTML body (Section 14) | Every image in the article body except the one already used as `PosterUrl`, deduped by resolved URL, capped at `max_images` setting. `Type = "Article"` for all (Wikipedia doesn't categorize images by role the way TMDB does front/back/still). |
| `ExtendedData` | see schema below | Sections array, skipped-section names, Wikidata ID, article URL, category list (untrimmed), image count before capping. |

### `ExtendedData` shape

```json
{
  "sections": [
    { "heading": null, "level": 0, "text": "The Batman is a 2022 American superhero film..." },
    { "heading": "Plot", "level": 2, "text": "In his second year of fighting crime, Batman..." },
    { "heading": "Cast", "level": 2, "text": "Robert Pattinson as Bruce Wayne / Batman..." },
    { "heading": "Production", "level": 2, "text": "" },
    { "heading": "Development", "level": 3, "text": "In 2013, Warner Bros. began..." }
  ],
  "skippedSections": ["References", "External links", "See also", "Further reading"],
  "wikipediaUrl": "https://en.wikipedia.org/wiki/The_Batman_(film)",
  "allCategories": ["2022 films", "American superhero films", "Films directed by Matt Reeves"],
  "imageCount": 14,
  "imagesIncluded": 14,
  "ids": { "wikidata": "Q64768688" },
  "bornDate": null,
  "diedDate": null
}
```

- `sections[].level` mirrors the HTML heading level (`h2` → 2, `h3` → 3, lead → 0) so the
  frontend's generic `JsonTree` renderer at least preserves nesting depth, even without
  typographic section styling (Section 4 of the architecture research — `PluginMetadataBox`
  renders nested objects/arrays structurally, not as formatted prose).
- `imageCount` vs `imagesIncluded` lets an admin see when the `max_images` cap actually dropped
  something, rather than silently looking like the article only had a few images.
- `ids.wikidata` is published so Chronicle's cross-reference cascade (Section 8) can seed a
  future Wikidata plugin automatically, per `PLUGIN_AUTHORING.md`'s "publishing cross-references"
  convention — Wikipedia becomes an authority for the one ID it's actually authoritative about.
- `bornDate`/`diedDate` are populated **only for the `people` type**, best-effort, via a regex
  over the lead section's first sentence — Wikipedia biography articles overwhelmingly open with
  `"{Full Name} (born {Month} {Day}, {Year})"` or, for the deceased,
  `"(born {...}; died {...})"`. Pattern: `\(born ([A-Z][a-z]+ \d{1,2}, \d{4})(?:; died ([A-Z][a-z]+ \d{1,2}, \d{4}))?\)`
  applied to the lead's plain text, parsed to ISO-8601 on success. This is a heuristic over
  prose, not a structured API field — it will miss unconventional openings (co-written bios,
  non-Western name order, articles that lead with a title/role instead of a birth date) and must
  fail silently (leave both `null`) rather than throw. For any type other than `people`, both
  fields are omitted entirely rather than emitted as `null` — most articles have no reason to
  carry them, and this keeps the general-purpose `ExtendedData` shape uncluttered for the common
  case.

---

## Section 8 — ExternalId Convention

**Format:** `wikipedia:{lang}:{title}` — title with spaces replaced by underscores, matching
Wikipedia's own canonical URL form (so `title` here is exactly what follows `/wiki/` in a
Wikipedia URL, still percent-encode-safe).

**Parsing in `GetByIdAsync`:**
```
parts = externalId.Split(':', 3)
if parts[0] != "wikipedia": throw ArgumentException
lang  = parts[1]
title = Uri.UnescapeDataString(parts[2])
```

**Pasted-URL handling (Fix Match):** recognize `https://{lang}.wikipedia.org/wiki/{title}` and
`https://{lang}.m.wikipedia.org/wiki/{title}` (mobile subdomain). Validate the host matches
`^([a-z0-9-]+\.)?wikipedia\.org$` (or the `m.` variant) **before** issuing any HTTP call — this
is the SSRF guard `PLUGIN_AUTHORING.md` requires for Fix Match URL normalization. Extract `lang`
from the subdomain (default `"en"` if just `www.wikipedia.org`) and `title` from the path
segment after `/wiki/`, URL-decoded. A bare `lang:Title` typed string (per `fixMatchHint`) is
parsed the same way as the native ExternalId format.

---

## Section 9 — Image Handling

- Wikipedia/Wikimedia Commons image URLs returned by both endpoints used here (`pageimages`
  API and the Parsoid HTML body) are **always full absolute URLs already** (`upload.wikimedia.org/...`)
  — no base-URL prefixing needed, unlike TMDB's path+size-code scheme.
- Size variants: Wikipedia's own thumbnail URLs embed a width directly in the path
  (`.../thumb/a/ab/File.jpg/300px-File.jpg`) — a plugin can request any width by substituting
  the number, but there's no need to: `pithumbsize=1000` (poster) and the in-article `<img>`
  `src` (already thumbnail-sized by the article's own rendering, typically 220–320px) give
  sensible sizes for `PosterUrl` vs. `AdditionalImages[].ThumbnailUrl` without extra requests.
- `PosterUrl` ← `original.source` from the pageimages call (6a) — Wikipedia's own "best single
  image" pick, not a heuristic over in-article images.
- `AdditionalImages` ← every other `<img>` in the parsed article body (Section 14), URL already
  full-size-ish thumbnail; `ThumbnailUrl` set to the same URL (no separate thumbnail fetch is
  needed since the in-article rendering is already thumbnail-scale).
- No Cover-Art-Archive-style separate image service — Wikipedia/Commons is the only source.

---

## Section 10 — Rate Limiting & Error Handling

Wikimedia publishes no hard numeric rate limit for a well-identified client on the public
API/REST endpoints (unlike MusicBrainz's documented 1 req/sec). Their actual guidance
([API etiquette](https://www.mediawiki.org/wiki/API:Etiquette)) is qualitative: **serialize
requests (no concurrency), set a real User-Agent, and don't hammer it** — heavy users are asked
to use the download dumps instead, not to self-throttle to a specific number.

Given that, and matching the house pattern (`MusicBrainzClient`/`FanEditRateLimiter`), the
design is a `SemaphoreSlim`-gated `WikipediaRateLimiter` with a **configurable floor, default
100ms** (10 req/s) — an order of magnitude more conservative than Wikimedia's own guidance
suggests is necessary, which is the right default for a shared, free, donation-funded service:

```csharp
internal sealed class WikipediaRateLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _last = Stopwatch.StartNew();
    private readonly int _floorMs;

    public WikipediaRateLimiter(int floorMs) => _floorMs = Math.Max(floorMs, 50);

    public async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var elapsed = _last.ElapsedMilliseconds;
            if (elapsed < _floorMs) await Task.Delay((int)(_floorMs - elapsed), ct);
            _last.Restart();
        }
        finally { _gate.Release(); }
    }
}
```

Every outbound call — search, pageimages, page/html, health check — routes through this one
gate on a single `HttpClient` instance, same shape as `MusicBrainzClient`.

- **Rate-limit response:** `429 Too Many Requests`, sometimes with a `Retry-After` header (in
  seconds). No documented JSON error body — treat any `429`/`503` as retryable.
- **Retry strategy:** honor `Retry-After` if present; otherwise exponential backoff starting at
  2s, doubling, capped at 16s, **max 3 retries** (kept lower than MusicBrainz's 4 — Chronicle's
  `ProviderCallGuard` hard-kills any provider call at 25s regardless, and `GetByIdAsync` here
  already makes two sequential requests before any retry logic, so a longer retry budget risks
  losing the whole call to the host timeout instead of failing cleanly).
- **Auth error:** not applicable — no auth.
- **Not Found:** `404` with `{"type": "...#pagenotfound", "title": "Not found."}` from the REST
  endpoint; `action=query` instead returns `"missing": true` on the page object (HTTP 200) —
  these are different shapes for the same condition and both must be checked.
- **Other errors:** `action=query` malformed-parameter errors return HTTP 200 with a top-level
  `"error"` object (Section 5) — always check for that key even on a 200 status.
- **Timeout:** `HttpClient.Timeout = TimeSpan.FromSeconds(15)`. Wikipedia is CDN-backed and
  normally fast; 15s leaves headroom under the 25s host guard for one retry on the slower of the
  two `GetByIdAsync` calls without blowing the ceiling on the first attempt.

---

## Section 11 — Scoring Strategy

Applies identically at every hierarchy level and every media type — the only per-level
difference is what gets folded into the search query (Section 5) and into the parent-name
corroboration bonus below. This directly addresses matching precision at the song/episode level,
where Wikipedia coverage is sparse and title collisions with common words are likely.

**Signals (search-result candidate vs. `MediaSearchContext`):**

1. **Title similarity** (0–45 pts): strip the candidate's disambiguation suffix (`" (film)"`,
   `" (TV series)"`, etc.) before comparing. Exact case-insensitive match against
   `PreciseName ?? Name` → 45. Otherwise, normalized token-set similarity (Jaccard or
   Levenshtein-ratio over lowercased, punctuation-stripped tokens) scaled: `45 * similarity`,
   floored to 0 below 0.5 similarity.
2. **Media-type keyword match against Wikidata short description** (`terms.description`)
   (0–25 pts, and a hard-reject path): match description text against a per-media-type keyword
   set —
   - `movies` → "film", "movie"
   - `tv` → "television series", "TV series", "anime television series"
   - `music` (root/artist level) → "singer", "band", "musician", "rapper", "musical group"
   - `music` (album level) → "album", "EP", "soundtrack album"
   - `music` (track level) → "song", "single"
   - `book` → "novel", "book", "graphic novel"
   - `game`/`video_game` → "video game"
   - `podcast` → "podcast"
   - `audiobook` → falls back to `book` keywords (Wikipedia rarely distinguishes the audio edition)
   - `people` → occupation phrases: "actor", "actress", "film director", "television director",
     "screenwriter", "film producer", "television producer", "musician", "singer", "voice actor",
     "comedian", "television presenter", "cinematographer", "film editor", "stunt performer".
     Wikidata short descriptions for people are almost always exactly this shape (Tom Cruise →
     "American actor and producer"; Anson Mount → "American actor"), which makes this signal
     unusually reliable for `people` compared to the media types above.

   Match → +25. No description present → +0 (neutral — many articles, especially for people/
   bands, have thin or absent Wikidata descriptions; don't penalize absence). **Description
   present but clearly names a conflicting type** (e.g. description contains "video game" while
   `MediaTypeName` is `movies`) → **hard-reject**, candidate excluded from results entirely, not
   just scored low — this is the main defense against title collisions.
3. **Disambiguation-page detection** (`pageprops.disambiguation` present) → **hard-reject**,
   same treatment as #2 — a disambiguation page is a list of links, not an article about the
   item.
4. **Year corroboration** (0–20 pts): if `context.Year` is present, look for a 4-digit year in
   the short description or the 3-sentence extract. Exact match → +20; off by exactly one
   (release-date-vs-announcement-year ambiguity is common) → +12; year present in context but
   not found anywhere in description/extract → +0 (neutral, not penalized — plenty of accurate
   articles don't restate the year in the first three sentences). For `people`, `context.Year` is
   ordinarily absent (a person search has no natural "year"), so this signal simply contributes
   0 and drops out — it does not need special-casing, it just rarely fires for this type.
5. **Parent/grandparent corroboration** (0–15 pts, hierarchy levels 1–2 only): if
   `context.ParentName` (or `GrandparentName` at level 2) appears as a substring of the 3-sentence
   extract → +15. This is the primary signal that keeps a level-2 search (e.g. one episode of a
   TV show, one track on an album) from matching an unrelated same-titled article — an episode
   article that doesn't even mention its own show in the first three sentences is very unlikely
   to be correct.

**Total capped at 100.** Minimum score to appear in the returned candidate list at all: **20**
(below that, don't bother returning it — even as a diagnostic, it's noise). Chronicle's own
default acceptance threshold (60) then decides whether a candidate is auto-applied; everything
between 20–59 still shows up in the Fix Match diagnostics panel.

**Practical effect at each level:**
- **Root level (movie, show, album, artist, book, game, person):** title + type-keyword + year
  alone regularly clears 60 for anything with a real article — this is the common, expected case.
  For `people` specifically this is usually the *most* reliable case, per Section 1 — the main
  failure mode isn't a missing article, it's a common name shared by multiple notable people with
  no distinguishing context to score against (Section 13).
- **Level 1 (season, disc):** Wikipedia essentially never has a dedicated article per season: a
  season is normally a section inside the show's own article, not a separate page. Expect this
  level to almost always return zero or low-score candidates — by design, not a bug. No special
  handling needed; scoring naturally reflects reality.
- **Level 2 (episode, track):** only surfaces a confident match for a genuinely notable
  episode/song with a standalone article. This is exactly the "unlikely to be many entries,
  though there could be, depending on significance" behavior you described — driven entirely by
  signal #5 (parent corroboration) plus title exactness, not by excluding the level.

---

## Section 12 — `MediaTypeSupport` Declaration

All entries except `people` have **empty `DisplayName`** (Wikipedia never owns/creates those
media types — see Section 1) and a deliberately high `DefaultPriority` number (=low actual
priority, per the "lower number = higher priority" convention) so Wikipedia sits behind every
type-specific provider until an admin explicitly re-ranks it in Metadata Assignment.

`people` is the one exception: per `docs/plans/2026-08-28-people-section-design.md`, Wikipedia
*is* the canonical registrant of this type — nothing else in Chronicle's plugin ecosystem
declares it, so its `DisplayName` is set (`"People"`), which triggers the normal
`PluginHostService.SyncMediaTypesFromPluginsAsync` upsert into `media_types` on next startup.
Its `DefaultPriority` stays at the same low `90` as every other row here regardless — being the
type's registrant doesn't mean Wikipedia should out-rank a future richer person-data source
(e.g. if a provider ever ships structured biographical data) for individual *fields*; registrant
and field-priority-winner are independent concerns, and Metadata Assignment still governs the
latter per-field, same as any other type.

| `MediaTypeName` | `DisplayName` | `SupportedFields` | `DefaultPriority` |
|---|---|---|---|
| `movies` | *(empty)* | `title, overview, poster_url, tags, extended_data` | `90` |
| `tv` | *(empty)* | `title, overview, poster_url, tags, extended_data` | `90` |
| `music` | *(empty)* | `title, overview, poster_url, tags, extended_data` | `90` |
| `book` | *(empty)* | `title, overview, poster_url, tags, extended_data` | `90` |
| `audiobook` | *(empty)* | `title, overview, poster_url, tags, extended_data` | `90` |
| `people` | `"People"` | `title, overview, poster_url, tags, extended_data, birth_date, death_date` | `90` |
| `game` | *(empty)* | `title, overview, poster_url, tags, extended_data` | `90` |
| `podcast` | *(empty)* | `title, overview, poster_url, tags, extended_data` | `90` |
| `fanedits` | *(empty)* | `title, overview, poster_url, tags, extended_data` | `90` |

`people`'s `SupportedFields` gains `birth_date, death_date` — the two canonical fields the
people-section design adds to `MetadataResolutionService.FieldMap`, populated from this plugin's
own `ExtendedData.bornDate`/`diedDate` (Section 7).

Every row but `people` uses identical `SupportedFields` and priority — reinforcing that this is
one generic capability declaration repeated per known type name, not eight separate
implementations. New media types introduced by future plugins (board games, comics, whatever)
get the same one-line treatment: add the name to this array, nothing else changes.

`HierarchyLevels`/`HierarchyLabels`/`InteractionVerb`/`ProgressUnit` are irrelevant on every
empty-`DisplayName` entry (they're only consulted when a plugin is the type's canonical
registrant) — left at defaults. `people` is the one row where they're live: `HierarchyLevels = 1`,
`HierarchyLabels = null`, `InteractionVerb = "viewed"`, `ProgressUnit = "percent"` — the latter
two are themselves inert in practice (per the people-section design, people are never tracked
through the watch/library/interaction system), but `MediaTypeSupport` requires some value, so
these are placeholders rather than meaningful settings.

---

## Section 13 — Edge Cases & Known Quirks

- **Disambiguation pages** — hard-rejected via `pageprops.disambiguation` (Section 11 #3), not
  left to title-similarity scoring to catch.
- **Redirects** — a REST `/page/html/` 404 on a title that search itself just returned is rare
  but possible (search index lag vs. live redirect table); resolved via one
  `action=query&redirects=1` retry (Section 6b).
- **Articles with no images at all** — common for stub-length articles, minor bands, obscure
  games. `PosterUrl` and `AdditionalImages` are simply empty/null; this is a normal, valid
  result, not an error — the card still shows title + overview + sections.
- **Very short "stub" articles** — lead section may be one or two sentences with no further
  headed sections at all (`sections` array has just the level-0 entry). Valid, just sparse.
- **List articles matching a level-2 search** — e.g. "List of Breaking Bad episodes" scoring
  against a single episode's search. Title similarity is low (list titles rarely match an
  episode name closely) and it carries no per-episode short description, so this is naturally
  suppressed by scoring rather than needing an explicit "is this a list page" filter.
- **Reference markers (`[1]`, `[citation needed]`) inline in prose** — Parsoid renders these as
  `<sup class="reference">` / `<sup class="noprint">` elements inline in `<p>` text; stripped
  entirely (not converted to text) during extraction (Section 14), since they're meaningless
  outside Wikipedia's own footnote UI.
- **Edit-section links, hatnotes, infobox/navbox tables, `<style>` blocks** — all present in the
  raw Parsoid HTML and all stripped before text extraction (Section 14) — none of these are
  "regular text."
- **Common-name collisions for `people`** — Wikipedia has no per-item context (which film, which
  role) to disambiguate a name like "Chris Evans" (actor vs. the unrelated British broadcaster of
  the same name) beyond the occupation-keyword signal (Section 11 #2), which only helps once the
  two candidates have different occupations — it does nothing for two people with the *same*
  occupation and name. `MediaSearchContext` carries no field that would help here (no "known for"
  hint, no co-starring cross-reference). Expect these to land in the 20–59 diagnostic band rather
  than auto-matching, and to need Fix Match. This is a real limitation of a name-only search
  against an encyclopedia with no disambiguating context supplied — not something scoring tuning
  can fully solve; the eventual people-section design may want to pass richer context (e.g. a
  known film credit to corroborate against) if it has one available.
- **Stage names / legal name redirects** — e.g. searching "Robert Downey Jr." typically resolves
  directly, but a variant spelling ("Robert Downey Junior") relies on Wikipedia's own redirect
  table, which search already accounts for since `generator=search` matches against redirects.
  No special handling needed beyond the existing redirect-retry in Section 6b.
- **Non-Latin / CJK / diacritic titles** — must be correctly percent-encoded in URLs and
  `Uri.UnescapeDataString`-decoded on the way back from a pasted Fix Match URL; .NET's
  `Uri`/`HttpUtility` handle this correctly by default as long as raw string concatenation into
  URLs is avoided.
- **Regional/language variance** — this plugin instance only ever queries the one configured
  `language` setting; it does not auto-detect the media item's language or fall back to other
  language editions. A library with mixed-language content needs the admin's judgment on which
  Wikipedia language is most useful, same as any single-language setting.
- **Copyright** — Wikipedia text is CC BY-SA / GFDL, which requires attribution on reuse; storing
  the raw extract text in `metadata_json` for Chronicle's own internal display is consistent
  with how Chronicle already stores TMDB/MusicBrainz text under their respective terms, but if
  this content is ever surfaced outside the authenticated app (e.g. a future public share page),
  attribution to Wikipedia/the specific article should accompany it. `ExtendedData.wikipediaUrl`
  exists specifically to make that attribution possible without a extra lookup.
- **Maintenance-category stoplist** (for `Tags` filtering, Section 7) — drop any category whose
  title starts with: `"CS1 "`, `"Articles with"`, `"Articles containing"`, `"Wikipedia articles"`,
  `"Pages using"`, `"Use mdy dates"`, `"Use dmy dates"`, `"Use British English"`,
  `"Use American English"`, `"All articles"`, `"Short description"`, `"Webarchive template"`,
  `"Commons category"`. This list will need occasional extension as Wikipedia's maintenance
  category naming evolves — not exhaustive by construction, only by observation.

---

## Section 14 — HTML Extraction Algorithm (Sections & Images)

This is the part with no existing Chronicle precedent (no plugin has modeled rich/long-form
text before this one) and no upstream API that hands back pre-split sections — it has to be
derived from the Parsoid HTML returned by Section 6b. Implemented with **HtmlAgilityPack**
(already a dependency in this codebase's plugin ecosystem — `Chronicle.Plugin.FanEdit` uses it
for its own HTML scraping; same `PackageReference Include="HtmlAgilityPack" Version="1.11.*"`
and the same `CopyLocalLockFileAssemblies=true` csproj setting FanEdit's `.csproj` documents —
without it the plugin loads fine but throws `FileNotFoundException` the first time it touches
HTML, because Chronicle's plugin loader expects `HtmlAgilityPack.dll` physically next to the
plugin DLL and doesn't consult `deps.json`).

```
doc = HtmlDocument.Load(htmlResponseStream)
body = doc.DocumentNode.SelectSingleNode("//body")

sections = []
skipped = []
allImages = []  // ordered, for later dedup + cap

foreach (section in body.SelectNodes(".//section[@data-mw-section-id]")):
    heading = section.SelectSingleNode("./h2|./h3|./h4")
    headingText = heading?.InnerText.Trim()
    level = heading == null ? 0 : int.Parse(heading.Name[1..])   // "h2" -> 2

    if headingText != null && BOILERPLATE_HEADINGS.Contains(headingText, OrdinalIgnoreCase):
        skipped.Add(headingText)
        continue   // still walk its <img> tags below before skipping text — images in a
                   // "Further reading" section's book covers are still legitimate images

    // strip non-prose noise before extracting text
    foreach (node in section.SelectNodes(
        ".//sup[contains(@class,'reference')] | .//span[contains(@class,'mw-editsection')] | " +
        ".//table | .//style | .//div[@role='note'] | .//div[contains(@class,'hatnote')]") ?? []):
        node.Remove()

    text = string.Join(" ", section.SelectNodes(".//p")?.Select(p => p.InnerText.Trim()) ?? [])
    text = HtmlEntity.DeEntitize(text).CollapseWhitespace()

    if headingText != null || text.Length > 0:   // keep lead (headingText null) even if short
        sections.Add(new { heading = headingText, level, text })

    foreach (img in section.SelectNodes(".//img") ?? []):
        src = img.GetAttributeValue("src", "")
        if src.StartsWith("//"): src = "https:" + src
        width = int.Parse(img.GetAttributeValue("data-file-width", "0"))
        if width > 0 && width < 50: continue   // icon-sized — flags, coordinate markers, edit icons
        allImages.Add(src)

allImages = allImages.Distinct().Where(url => url != posterUrl).ToList()
imageCount = allImages.Count
imagesIncluded = allImages.Take(maxImagesSetting).ToList()
```

`BOILERPLATE_HEADINGS` (case-insensitive exact match against the heading text): `References`,
`External links`, `See also`, `Further reading`, `Notes`, `Bibliography`, `Citations`,
`Sources`, `Works cited`, `Footnotes`. This list is deliberately short and exact-match rather
than pattern-matched — a heading like "References in popular culture" is legitimate prose and
must not be caught by a substring match against "References".

The lead section (`data-mw-section-id="0"`) never has an `<h2>`, so `heading` is `null` and
`level` is `0` — matches the `ExtendedData.sections` shape in Section 7, and its `text` becomes
`MediaMetadata.Overview` directly (first element of `sections`, not a separate fetch).

---

## Open items for implementation (not blocking this design)

- Exact Jaccard/Levenshtein library choice for title-similarity scoring (Section 11 #1) — any
  small dependency-free implementation is fine; no existing Chronicle plugin has a shared fuzzy-
  match helper to reuse (each rolls its own).
- Whether `resync-all-metadata`'s more-frequent-than-usual cadence (weekly, vs. some providers'
  monthly) is worth calling out to users in the description — articles do change often, but a
  full resync re-issues the full two-request `GetByIdAsync` sequence per matched item, so a very
  large library could produce a noticeably long weekly task run under the 100ms floor. Consider
  surfacing estimated run time in the background task's description once the item-count is known
  at implementation time.
