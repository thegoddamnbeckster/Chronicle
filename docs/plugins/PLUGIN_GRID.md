# Chronicle.Plugin.GRid — Design Document

**Plugin ID:** `chronicle.plugin.grid`
**Version:** 1.0.0
**Media Types:** Albums (`album`), Music (`music`)
**Auth:** None (public lookup via DDEX portal)
**API:** DDEX / GRid Registry — `https://grid.ddex.net`

---

## Purpose

The [Global Release Identifier](https://grid.ddex.net/) (GRid) is the
international standard identifier for music releases in digital distribution
(ISO 17316). Every digital release — album, EP, single — distributed through
digital stores has a GRid issued by DDEX (Digital Data Exchange). This plugin
resolves GRids to release metadata, providing an authoritative digital
distribution identifier to complement UPC/EAN (physical releases) and
MusicBrainz release IDs.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `album` | 8 | GRid-to-release resolution |
| `music` | 9 | Single track release GRid |

---

## API Overview

The GRid registry provides a public web interface and limited API:

| URL | Description |
|-----|-------------|
| `https://grid.ddex.net/grid/{grid_value}` | GRid lookup page |

GRid format: `A1-{issuer}-{release}-{check}` (18-character string)
Example: `A1-2425G-ABC1234002-M`

The registry returns: issuer name, release title, artist, release date,
label, associated ISRCs.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 1000 |

---

## Fields Populated

```
title, year, cast (artists),
grid_id,
metadata_json: { grid, issuer_name, label_name,
                 release_date, associated_isrcs }
```

---

## Rate Limits

- Public portal; minimum 1,000 ms between requests
- Cache GRid lookups indefinitely — GRids are permanent identifiers

---

## Implementation Notes

- GRid is a **digital distribution identifier** — it identifies the
  release package as distributed to stores, not the physical product
  (UPC/EAN) or the recordings themselves (ISRC)
- GRid is most useful when received from a digital distributor metadata
  feed — rarely searched for independently
- The issuer code (7-character alphanumeric) identifies the DDEX member
  who issued the GRid (typically the distributor: TuneCore, DistroKid, etc.)
- `associated_isrcs` links the release back to individual recordings
  — harvest these for cross-referencing
- GRid is not yet widely surfaced in consumer-facing music databases;
  this plugin is primarily relevant for distributors, labels, and
  professional catalogue management use cases
- Store GRid in `media_external_ids` with source `grid`

---

## Scaffold Location

```
Chronicle.Plugin.GRid/
├── Chronicle.Plugin.GRid.csproj
├── README.md
├── manifest.json
├── GRidPlugin.cs
└── Models/
    └── GRidRelease.cs
```
