# Chronicle.Plugin.IsThereAnyDeal — Design Document

**Plugin ID:** `chronicle.plugin.isthereanydeal`
**Version:** 1.0.0
**Media Types:** Games (`game`)
**Auth:** API key (free at isthereanydeal.com/dev/)
**API:** IsThereAnyDeal REST API v2 — `https://api.isthereanydeal.com`

---

## Purpose

[IsThereAnyDeal (ITAD)](https://isthereanydeal.com/) aggregates game pricing
and sale history across 30+ PC gaming stores (Steam, GOG, Epic, Humble, etc.).
This Chronicle plugin enriches game entries with current pricing, historical
low prices, deal alerts, and store availability data. It is a
**pricing/availability** enrichment plugin rather than a primary metadata source.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `game` | 8 | Pricing and store availability enrichment |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| Game lookup by title | `GET /v02/game/search/?q={title}&key={key}` |
| Game info | `GET /v02/game/info/?plain={game_id}&key={key}` |
| Current prices | `GET /v02/game/prices/?plains={ids}&key={key}` |
| Historical low | `GET /v02/game/lowest/?plains={ids}&key={key}` |
| Store list | `GET /v02/web/stores/?key={key}` |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | ITAD API Key | Password | Yes | isthereanydeal.com/dev/ |
| `country` | Country | Dropdown | No | ISO 3166-1 alpha-2, default: `US` |
| `stores_filter` | Limit to Stores | MultiSelect | No | Filter by store (steam, gog, epic, etc.) |
| `include_historical_low` | Include Historical Low | Boolean | No | Default: true |

---

## Fields Populated

```
metadata_json: { current_prices: [{ store, price, url, currency }],
                 historical_low: { price, store, date },
                 store_availability: [{ store, available, drm }],
                 itad_plain_id }
```

---

## Rate Limits

- Free API: 100 req/hr
- Cache prices for 1 hour; historical lows for 24 hours

---

## Implementation Notes

- ITAD uses a "plain" identifier (slug) for games rather than numeric IDs;
  store as `itad_plain_id` in `media_external_ids` with source `itad`
- Match ITAD games to Chronicle entries via Steam AppID cross-reference —
  ITAD's search supports filtering by Steam AppID
- DRM information per store is particularly useful: values include `Steam`,
  `DRM-Free`, `GOG Galaxy`, etc.
- Price values are in the requested country's local currency
- ITAD can also provide deal notifications — a future enhancement could
  use the `/v02/user/deals/wait/` webhook API for deal alerts

---

## Scaffold Location

```
Chronicle.Plugin.IsThereAnyDeal/
├── Chronicle.Plugin.IsThereAnyDeal.csproj
├── README.md (this document)
├── manifest.json
├── IsThereAnyDealPlugin.cs
└── Models/
    ├── ItadGame.cs
    ├── ItadPrice.cs
    └── ItadHistoricalLow.cs
```
