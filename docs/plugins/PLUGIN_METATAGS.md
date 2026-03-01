# Chronicle.Plugin.MetaTags — Design Document

**Plugin ID:** `chronicle.plugin.metatags`
**Version:** 1.0.0
**Media Types:** Web links (`weblink`), Generic media (`media`)
**Auth:** None
**API:** metatags.io API — `https://metatags.io` / local HTML parsing

---

## Purpose

[Meta Tags (metatags.io)](https://metatags.io/) is a meta tag inspector and
preview tool. This Chronicle plugin extracts a comprehensive set of HTML
meta tags from any URL — including Open Graph, Twitter Cards, JSON-LD
structured data, and standard `<meta>` tags — to build rich metadata records
for web-linked media items.

This is a superset of the OpenGraph plugin, adding JSON-LD and Twitter Card
support.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `weblink` | 2 | Full meta tag extraction from any URL |

---

## Data Sources Extracted

### 1. Standard HTML meta tags
```html
<meta name="title" content="...">
<meta name="description" content="...">
<meta name="keywords" content="...">
<meta name="author" content="...">
```

### 2. Open Graph tags
```html
<meta property="og:title" content="...">
<meta property="og:description" content="...">
<meta property="og:image" content="...">
```

### 3. Twitter Card tags
```html
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:title" content="...">
<meta name="twitter:description" content="...">
<meta name="twitter:image" content="...">
```

### 4. JSON-LD structured data
```json
{
  "@context": "https://schema.org",
  "@type": "Movie",
  "name": "...",
  "description": "...",
  "datePublished": "...",
  "director": { "@type": "Person", "name": "..." }
}
```

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `user_agent` | User Agent | Text | No | Browser UA string |
| `timeout_seconds` | Timeout | Number | No | Default: 10 |
| `extract_json_ld` | Parse JSON-LD | Boolean | No | Default: true |
| `extract_twitter_cards` | Parse Twitter Cards | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview, poster_url, year, genres, cast, directors,
metadata_json: { keywords, author, canonical_url, og_type,
                 twitter_card, json_ld_type, schema_org_data }
```

---

## Rate Limits

- No rate limits for local HTML scraping
- Add 500 ms delay between requests to avoid overloading servers

---

## Implementation Notes

- JSON-LD is embedded as `<script type="application/ld+json">` — parse
  with `System.Text.Json` after extracting the script content
- Schema.org types of interest: `Movie`, `TVSeries`, `TVEpisode`,
  `MusicAlbum`, `MusicRecording`, `Book`, `VideoGame`, `Article`
- Priority order when multiple sources have the same field:
  JSON-LD > Open Graph > Twitter Card > standard meta
- Share `AngleSharp` HTML parsing infrastructure with the OpenGraph plugin
  (consider a shared `Chronicle.Plugin.WebScraping.Core` NuGet)

---

## Scaffold Location

```
Chronicle.Plugin.MetaTags/
├── Chronicle.Plugin.MetaTags.csproj
├── README.md (this document)
├── manifest.json
├── MetaTagsPlugin.cs
└── Models/
    ├── MetaTagData.cs
    └── JsonLdDocument.cs
```
