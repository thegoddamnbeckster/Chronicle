# Chronicle.Plugin.Crossref — Design Document

**Plugin ID:** `chronicle.plugin.crossref`
**Version:** 1.0.0
**Media Types:** Books (`book`), Academic papers (`paper`)
**Auth:** None required; polite pool email optional
**API:** Crossref REST API — `https://api.crossref.org`

---

## Purpose

[Crossref](https://www.crossref.org/) is the official DOI registration agency
for academic and professional publications. Its REST API provides metadata for
130+ million scholarly works identified by DOI — including books, journal
articles, conference papers, and reports. This plugin enables Chronicle to
track academic books and papers with authoritative DOI-based metadata.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `book` | 4 | Academic and scholarly books |
| `paper` | 1 | Journal articles and conference papers |

---

## API Overview

| Operation | Endpoint |
|-----------|---------|
| DOI lookup | `GET /works/{doi}` |
| Search works | `GET /works?query={q}&rows={n}` |
| Search by author | `GET /works?query.author={name}` |
| Search by title | `GET /works?query.title={title}` |
| Journal detail | `GET /journals/{issn}` |
| Funder lookup | `GET /funders/{id}` |

Add `mailto={email}` query param to join the polite pool (faster access).

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `polite_email` | Contact Email (Polite Pool) | Text | No | Strongly recommended |
| `include_references` | Fetch References | Boolean | No | Default: false |
| `include_abstract` | Fetch Abstract | Boolean | No | Default: true |

---

## Fields Populated

```
title, overview (abstract), year (published), genres (subject/type),
cast (authors), publisher, isbn, doi, issn,
page_count, language, license, citation_count,
metadata_json: { type, container_title, volume, issue,
                 page_range, funder, references }
```

---

## Rate Limits

- Polite pool (with `mailto`): 50 req/sec
- Without email: ~3 req/sec, may be throttled
- Cache DOI lookups indefinitely — DOI metadata is immutable

---

## Implementation Notes

- DOI is the canonical identifier for academic works; store in
  `media_external_ids` with source `crossref`
- Work types: `journal-article`, `book`, `book-chapter`,
  `proceedings-article`, `dataset`, `report`, `standard`
- Authors are structured as `{ given, family, ORCID }` — combine
  as `"{given} {family}"` for the `cast` field
- Abstract may be null for older works or works where the publisher
  has not deposited it with Crossref
- The `license` array contains usage rights — useful for open access detection

---

## Scaffold Location

```
Chronicle.Plugin.Crossref/
├── Chronicle.Plugin.Crossref.csproj
├── README.md (this document)
├── manifest.json
├── CrossrefPlugin.cs
└── Models/
    ├── CrossrefWork.cs
    └── CrossrefAuthor.cs
```
