# Chronicle.Plugin.EPGData — Design Document

**Plugin ID:** `chronicle.plugin.epgdata`
**Version:** 1.0.0
**Media Types:** TV (`tv`), Broadcast schedules (`tv_schedule`)
**Auth:** API key (registration at epgdata.com)
**API:** EPGdata.tv XML / REST API — `https://www.epgdata.com`

---

## Purpose

[EPGdata.tv](https://www.epgdata.com/) is a European Electronic Programme Guide
(EPG) data provider offering TV schedule and programme metadata for a large
number of channels across Germany, Austria, Switzerland, and other European
countries. It is widely used as a data source for media centre software
(e.g., Kodi/XMLTV).

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `tv` | 4 | TV programme metadata |
| `tv_schedule` | 2 | European broadcast EPG |

---

## API Overview

EPGdata primarily distributes data as compressed XMLTV files. The Chronicle
plugin will parse the XMLTV format rather than make real-time API calls.

| Operation | Format |
|-----------|--------|
| Download EPG data | Gzipped XMLTV file from subscription URL |
| Channel list | Embedded in XMLTV header `<channel>` elements |
| Programme list | `<programme>` elements in XMLTV |

XMLTV schema fields used:

```xml
<programme start="20260228130000 +0100" stop="20260228140000 +0100" channel="das-erste.de">
  <title lang="de">Titel</title>
  <desc lang="de">Beschreibung</desc>
  <category lang="en">Documentary</category>
  <episode-num system="xmltv_ns">0.5.0/1</episode-num>
  <icon src="https://..." width="200" height="300"/>
  <rating><value>FSK 12</value></rating>
</programme>
```

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | EPGdata API Key | Password | Yes | From epgdata.com subscription |
| `subscription_url` | EPG Download URL | Url | Yes | Provided by EPGdata |
| `language` | Language | Dropdown | No | `de`, `en` — default: `de` |
| `channels` | Channel Filter | TextArea | No | One channel ID per line |
| `refresh_hours` | Refresh Interval (hours) | Number | No | Default: 24 |

---

## Fields Populated

```
title, overview, genres, cast, director, broadcast_channel,
broadcast_start, broadcast_end, episode_number, season_number,
content_rating, epgdata_programme_id
```

---

## Rate Limits

- Data is downloaded as bulk files (not real-time API calls)
- Refresh no more than once per 24 hours per subscription terms
- Cache downloaded XMLTV files locally; parse on demand

---

## Implementation Notes

- EPGdata subscriptions provide access to a zipped XMLTV bundle
- Use `System.Xml.Linq` or a dedicated XMLTV parser to read data
- The XMLTV `<episode-num system="xmltv_ns">` format encodes
  season/episode as `season.episode.part` (0-indexed)
- Store the EPGdata programme ID in `media_external_ids` with
  source `epgdata`
- Consider integrating with the Kodi plugin for shared EPG data

---

## Scaffold Location

```
Chronicle.Plugin.EPGData/
├── Chronicle.Plugin.EPGData.csproj
├── README.md (this document)
├── manifest.json
├── EPGDataPlugin.cs
└── Models/
    ├── XmltvProgramme.cs
    └── XmltvChannel.cs
```
