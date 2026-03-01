# Chronicle.Plugin.JustWatch — Design Document

**Plugin ID:** `chronicle.plugin.justwatch`
**Version:** 1.0.0
**Media Types:** Movies (`movie`), TV (`tv`)
**Auth:** None (public GraphQL API)
**API:** JustWatch public GraphQL — `https://apis.justwatch.com/graphql`

---

## Purpose

[JustWatch](https://www.justwatch.com/) aggregates streaming availability data
for movies and TV shows across all major platforms (Netflix, Disney+, Amazon,
Apple TV+, etc.). This plugin enriches Chronicle entries with "where to watch"
data — streaming URLs, buy/rent prices, and provider availability by region.

This is a **streaming availability** plugin, not a primary metadata source.
It should run as a secondary enrichment pass after TMDB/TheTVDB have already
populated core fields.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `movie` | 8 | Streaming availability enrichment |
| `tv` | 8 | Streaming availability enrichment |

---

## API Overview

JustWatch exposes a public (undocumented) GraphQL API.

```graphql
query GetSuggestedTitles($country: Country!, $language: Language!, $first: Int!, $filter: TitleFilter!) {
  popularTitles(country: $country, first: $first, filter: $filter) {
    edges {
      node {
        id
        objectType
        content(country: $country, language: $language) {
          title
          fullPath
          posterUrl
          offers {
            monetizationType
            retailPrice(language: $language)
            currency
            package { packageId shortName clearName }
            standardWebURL
          }
        }
      }
    }
  }
}
```

| Operation | Method |
|-----------|--------|
| Title search | GraphQL `searchTitles` |
| Title detail + offers | GraphQL `title` by node ID |
| Provider list | GraphQL `packages` by country |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `country` | Country | Dropdown | Yes | `US`, `GB`, `DE`, `AU`, etc. |
| `language` | Language | Dropdown | No | Default: `en` |
| `monetization_types` | Show Offer Types | MultiSelect | No | `FLATRATE`, `BUY`, `RENT`, `FREE` |
| `providers_filter` | Limit to Providers | Text | No | Comma-sep provider short names |

---

## Fields Populated

```
streaming_providers, streaming_urls, buy_price, rent_price,
justwatch_id, justwatch_url
```

> These are stored in the item's `metadata_json` — not in `MediaMetadata`
> core fields, since `IMetadataProvider` does not have a dedicated
> streaming-availability field set.

---

## Rate Limits

- No official rate limit published; use polite delays (500 ms between requests)
- JustWatch has blocked scrapers before — send realistic User-Agent headers
- Cache offers aggressively (they update at most once per day)

---

## Implementation Notes

- JustWatch's GraphQL schema is not officially published; reverse-engineer
  from browser network traffic if the endpoints change
- `objectType` values: `MOVIE`, `SHOW`
- Node IDs are JustWatch-internal; map to TMDB/IMDb IDs via
  `externalIds` in the GraphQL response
- Offer `monetizationType`: `FLATRATE` = subscription streaming,
  `BUY`, `RENT`, `FREE`, `ADS`
- Poster URL pattern: `https://images.justwatch.com/poster/{profile}/{id}.jpg`
  where `profile` = `s166` (small), `s276`, `s592` (large)

---

## Scaffold Location

```
Chronicle.Plugin.JustWatch/
├── Chronicle.Plugin.JustWatch.csproj
├── README.md (this document)
├── manifest.json
├── JustWatchPlugin.cs
└── Models/
    ├── JustWatchTitle.cs
    ├── JustWatchOffer.cs
    └── JustWatchProvider.cs
```
