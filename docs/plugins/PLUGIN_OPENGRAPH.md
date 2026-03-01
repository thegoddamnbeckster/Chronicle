# Chronicle.Plugin.OpenGraph — Design Document

**Plugin ID:** `chronicle.plugin.opengraph`
**Version:** 1.0.0
**Media Types:** Web links (`weblink`), Generic media (`media`)
**Auth:** None
**API:** OpenGraph.xyz API — `https://opengraph.xyz/api/{url}` (or local parsing)

---

## Purpose

This plugin extracts [Open Graph Protocol](https://ogp.me/) metadata from
any URL. Open Graph (`og:`) tags are used by virtually every major website
(YouTube, Wikipedia, news sites, social media) to describe their content.
When a user adds a URL to Chronicle, this plugin fetches the OG metadata to
populate title, description, thumbnail, and type information automatically.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `weblink` | 1 | Any URL — OG tags scraped from the page |
| `movie` | 9 | Fallback when TMDB/IMDb have no result |
| `tv` | 9 | Fallback |
| `music` | 9 | Fallback |
| `book` | 9 | Fallback |

---

## API Approaches

**Option A — OpenGraph.xyz API (zero-setup):**

```
GET https://opengraph.xyz/api/{encoded_url}
```

Returns structured JSON with OG tags pre-parsed.

**Option B — Local scraping (preferred, no dependency):**

Fetch the URL with `HttpClient` and parse `<meta property="og:*">` tags
from the HTML `<head>` using `HtmlAgilityPack` or `AngleSharp`.

Key tags mapped:

| OG Tag | Maps to |
|--------|---------|
| `og:title` | `title` |
| `og:description` | `overview` |
| `og:image` | `poster_url` |
| `og:url` | `metadata_json.canonical_url` |
| `og:type` | `metadata_json.og_type` |
| `og:site_name` | `metadata_json.site_name` |
| `og:video:duration` | `runtime_minutes` |
| `article:published_time` | `year` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `extraction_mode` | Extraction Mode | Dropdown | No | `local`, `opengraph_xyz` — default: `local` |
| `user_agent` | User Agent | Text | No | Browser UA for scraping |
| `timeout_seconds` | Request Timeout | Number | No | Default: 10 |
| `follow_redirects` | Follow Redirects | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview, poster_url, year (from article:published_time),
metadata_json: { og_type, canonical_url, site_name,
                 twitter_card, twitter_title, twitter_image }
```

---

## Rate Limits

- No rate limits for local scraping
- Be polite: respect `robots.txt`, add `500 ms` delay between requests
- Some sites block scrapers; send realistic browser User-Agent headers

---

## Implementation Notes

- Use `AngleSharp` (MIT licensed) for HTML parsing — it handles
  malformed HTML better than `HtmlAgilityPack`
- After OG tags, fall back to `<title>` and `<meta name="description">`
- `og:type` values: `website`, `article`, `video.movie`, `video.tv_show`,
  `video.episode`, `music.song`, `music.album`, `book`
- This plugin should run on any URL Chronicle encounters that isn't already
  handled by a more specific provider

---

## Scaffold Location

```
Chronicle.Plugin.OpenGraph/
├── Chronicle.Plugin.OpenGraph.csproj
├── README.md (this document)
├── manifest.json
├── OpenGraphPlugin.cs
└── Models/
    └── OpenGraphData.cs
```
