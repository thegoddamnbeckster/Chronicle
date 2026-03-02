# Chronicle.Plugin.AmazonMusic — Design Document

**Plugin ID:** `chronicle.plugin.amazonmusic`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (Product Advertising API requires AWS credentials, not used here)
**API:** Amazon Music — `https://music.amazon.com` (scraping + Amazon ASIN lookup)

---

## Purpose

[Amazon Music](https://music.amazon.com/) is Amazon's streaming service,
tightly integrated with Alexa and Prime. While Amazon Music has no public
metadata API, Amazon product pages for digital music use stable ASIN
identifiers, and limited metadata is available via the Amazon Product
Advertising API (PA API 5.0) under the `Music` node. This plugin uses
ASIN-based lookups to cross-reference Amazon Music catalogue entries
with ASINs from other sources (e.g. Discogs, MusicBrainz).

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 8 | Track ASIN cross-reference |
| `album` | 8 | Album ASIN cross-reference |
| `artist` | 8 | Artist page reference |

---

## Data Available

| Source | Method | Data |
|--------|--------|------|
| PA API 5.0 `/paapi5/getitems` | REST (AWS SigV4 auth) | Title, artist, ASIN, cover art, release date |
| `music.amazon.com` pages | Scraping | Same + listener counts |

PA API request (ItemIds lookup):
```json
{
  "ItemIds": ["{asin}"],
  "Resources": ["ItemInfo.Title", "ItemInfo.ByLineInfo",
                 "Images.Primary.Large", "ItemInfo.ContentInfo"],
  "PartnerTag": "{associate_tag}",
  "PartnerType": "Associates",
  "Marketplace": "www.amazon.com"
}
```

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `access_key` | AWS Access Key | Password | No | For PA API 5.0 |
| `secret_key` | AWS Secret Key | Password | No | For PA API 5.0 |
| `associate_tag` | Associates Tag | Text | No | Required by PA API |
| `marketplace` | Marketplace | Dropdown | No | Default: `www.amazon.com` |

---

## Fields Populated

```
title, year, cast (artists), poster_url,
amazon_asin, amazon_url,
metadata_json: { asin, amazon_music_url, release_date }
```

---

## Rate Limits

- PA API 5.0: 1 req/sec; burst up to 10 req/sec with throttling
- Cache all data for 7 days

---

## Implementation Notes

- Amazon Music does not have a catalogue search API — this plugin
  is purely a **cross-reference enricher**, not a primary search source
- The best workflow: obtain an ASIN from another source (e.g. Discogs
  `asin` field), then use PA API to fetch cover art and release date
- PA API requires AWS SigV4 request signing — implement using
  standard HMAC-SHA256 signing without third-party AWS SDK
- If PA API credentials are not configured, the plugin can fall back
  to scraping `music.amazon.com/albums/{asin}` for basic metadata
- Amazon Music Ultra HD albums have the `ULTRA_HD` badge visible in
  page HTML — store as `metadata_json.ultra_hd: true`
- ASIN is the stable cross-reference ID — store in `media_external_ids`
  with source `amazon`

---

## Scaffold Location

```
Chronicle.Plugin.AmazonMusic/
├── Chronicle.Plugin.AmazonMusic.csproj
├── README.md
├── manifest.json
├── AmazonMusicPlugin.cs
└── Models/
    └── AmazonMusicItem.cs
```
