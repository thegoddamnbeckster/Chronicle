# Chronicle.Plugin.WebDev — Design Document

**Plugin ID:** `chronicle.plugin.webdev`
**Version:** 1.0.0
**Media Types:** Web links (`weblink`)
**Auth:** None
**API:** web.dev / PageSpeed Insights API — `https://www.googleapis.com/pagespeedonline/v5/runPagespeed`

---

## Purpose

[web.dev](https://web.dev/) is Google's developer resource for web quality.
Its underlying engine — Lighthouse / PageSpeed Insights — provides structured
quality and metadata analysis for any URL, including performance scores, SEO
data, structured data validation, accessibility data, and Open Graph/Twitter
card metadata.

This Chronicle plugin uses the **PageSpeed Insights API** (free, Google API key
required) to extract metadata quality signals for tracked web content.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `weblink` | 9 | Web page quality and meta analysis |

---

## API Overview

```
GET https://www.googleapis.com/pagespeedonline/v5/runPagespeed
    ?url={url}
    &strategy=desktop
    &key={api_key}
    &category=seo
```

Key data extracted from Lighthouse response:

| Lighthouse Audit | Maps to |
|-----------------|---------|
| `document-title` | `title` |
| `meta-description` | `overview` |
| `structured-data` | `metadata_json.json_ld` |
| `link-text` | — |
| `crawlable-anchors` | — |
| `is-crawlable` | `metadata_json.is_crawlable` |
| `hreflang` | `metadata_json.languages` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Google API Key | Password | Yes | PageSpeed Insights API |
| `strategy` | Device Strategy | Dropdown | No | `desktop`, `mobile` — default: `desktop` |
| `include_scores` | Include Performance Scores | Boolean | No | Default: false |

---

## Fields Populated

```
title, overview,
metadata_json: { seo_score, performance_score, accessibility_score,
                 best_practices_score, json_ld, is_crawlable,
                 languages, canonical_url }
```

---

## Rate Limits

- PageSpeed Insights API: 25,000 queries/day (free)
- Requests are slow (3–10 s each due to full page analysis)
- Cache results aggressively — re-run at most once per week per URL

---

## Implementation Notes

- The PageSpeed/Lighthouse API is the proper way to access web.dev's
  analysis programmatically — there is no separate web.dev API
- Set `category=seo` to limit response size; add `accessibility`,
  `performance` if the user enables score collection
- This plugin is primarily a metadata quality tool, not a content
  enrichment tool — it tells you *about* a web page rather than
  providing media metadata *from* it
- Consider whether this is more of a diagnostic/report plugin than a
  metadata provider; it may be better implemented as an `IReportPlugin`

---

## Scaffold Location

```
Chronicle.Plugin.WebDev/
├── Chronicle.Plugin.WebDev.csproj
├── README.md (this document)
├── manifest.json
├── WebDevPlugin.cs
└── Models/
    └── LighthouseResult.cs
```
