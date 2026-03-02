# Chronicle.Plugin.SESAC — Design Document

**Plugin ID:** `chronicle.plugin.sesac`
**Version:** 1.0.0
**Media Types:** Music (`music`), Artists (`artist`)
**Auth:** None (public repertoire search)
**API:** SESAC Repertory — `https://www.sesac.com/repertory`

---

## Purpose

[SESAC](https://www.sesac.com/) is the third US performing rights organisation
(PRO), smaller than ASCAP and BMI but operating on an invitation-only basis
with a curated roster of notable songwriters. It represents a significant
portion of Christian music, country, and selective pop/R&B catalogues.
This plugin completes the trifecta of US PRO coverage alongside the ASCAP
and BMI plugins.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 7 | Songwriting credits for SESAC-registered works |
| `artist` | 8 | Songwriter PRO registration |

---

## API Overview

SESAC provides a public repertory search:

| Endpoint | Description |
|----------|-------------|
| `GET https://www.sesac.com/repertory/search?title={title}&artist={artist}` | Search |

Response: HTML or JSON (depending on Accept header)

Unofficial JSON endpoint:
```
GET https://www.sesac.com/api/repertory/search?
    title={title}&artist={artist}&page=1&perPage=10
```

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `request_delay_ms` | Request Delay (ms) | Number | No | Default: 1500 |

---

## Fields Populated

```
title, cast (writers + publishers),
sesac_work_id, sesac_url,
metadata_json: { iswc, writers: [{ name, role }],
                 publishers: [{ name }] }
```

---

## Rate Limits

- Public endpoint; minimum 1,500 ms between requests
- Cache credits for 30 days

---

## Implementation Notes

- SESAC is the **last resort** in the ASCAP → BMI → SESAC PRO lookup chain
- SESAC's roster skews toward Christian contemporary, select country
  acts, and notable singer-songwriters — valuable for completeness
- The public search interface may require a User-Agent header that
  resembles a browser to avoid bot blocking
- ISWC cross-reference links all three PRO databases for the same work
- SESAC's invitation-only nature means its catalogue is smaller but
  the works registered are often high-profile

---

## Scaffold Location

```
Chronicle.Plugin.SESAC/
├── Chronicle.Plugin.SESAC.csproj
├── README.md
├── manifest.json
├── SESACPlugin.cs
└── Models/
    └── SesacWork.cs
```
