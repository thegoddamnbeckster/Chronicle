# Chronicle.Plugin.BMI — Design Document

**Plugin ID:** `chronicle.plugin.bmi`
**Version:** 1.0.0
**Media Types:** Music (`music`), Artists (`artist`)
**Auth:** None (public repertoire search)
**API:** BMI Repertoire — `https://repertoire.bmi.com`

---

## Purpose

[BMI](https://www.bmi.com/) (Broadcast Music, Inc.) is one of the three major
US performing rights organisations, representing over 1.4 million songwriters,
composers, and publishers. Its public repertoire database provides verified
songwriting and publishing credits for BMI-registered works. This plugin
complements the ASCAP and SESAC plugins to provide comprehensive PRO coverage
across the US music rights ecosystem.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 7 | Songwriting credits for BMI-registered works |
| `artist` | 8 | Songwriter/composer PRO registration |

---

## API Overview

BMI's repertoire search is available as a public web service:

| Endpoint | Description |
|----------|-------------|
| `GET https://repertoire.bmi.com/startPage` | Search landing page |
| `POST https://repertoire.bmi.com/Ttle` | Title search (form POST) |

HTML form parameters:
```
action=SEARCH&type=WORK&
title={title}&artist={artist}&
page=1&results=10
```

Response: HTML table with work title, writers, publishers, BMI Work #

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 1500 |
| `include_publishers` | Fetch Publisher Data | Boolean | No | Default: true |

---

## Fields Populated

```
title, cast (writers + publishers),
bmi_work_id, bmi_url,
metadata_json: { iswc, writers: [{ name, role }],
                 publishers: [{ name }],
                 bmi_work_number }
```

---

## Rate Limits

- Public endpoint; minimum 1,500 ms between requests
- Cache credits for 30 days

---

## Implementation Notes

- BMI is used in the same workflow as ASCAP — try ASCAP first, then BMI
  if no match found (a song is registered with exactly one US PRO)
- ISWC may be present in BMI results — store in `media_external_ids`
- BMI Work # is BMI's own identifier (not globally standardised)
- The search interface is HTML-form based — parse the result table
  rather than expecting a JSON response
- Cross-reference ASCAP → BMI → SESAC in priority order to find the
  registering PRO for any given song
- BMI handles a large share of country, urban, and contemporary pop —
  complement ASCAP's rock/classical focus

---

## Scaffold Location

```
Chronicle.Plugin.BMI/
├── Chronicle.Plugin.BMI.csproj
├── README.md
├── manifest.json
├── BMIPlugin.cs
└── Models/
    └── BmiWork.cs
```
