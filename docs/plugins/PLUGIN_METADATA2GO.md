# Chronicle.Plugin.Metadata2Go — Design Document

**Plugin ID:** `chronicle.plugin.metadata2go`
**Version:** 1.0.0
**Media Types:** Generic web content (`web`), Documents (`document`)
**Auth:** None (public web service)
**API:** Metadata2Go.com web extraction — `https://www.metadata2go.com`

---

## Purpose

[Metadata2Go.com](https://www.metadata2go.com/) is an online metadata viewer
and extractor for files and URLs. It extracts metadata from images, documents,
audio, and video files via a web interface. This Chronicle plugin wraps the
Metadata2Go service (or its underlying extraction logic) to provide metadata
enrichment for user-submitted files and URLs.

> **Note:** Metadata2Go does not publish a public API. This plugin either
> (a) wraps the web interface via form submission, or (b) replicates its
> extraction approach locally using open-source libraries. Option (b) is
> strongly preferred for reliability.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `web` | 8 | URL metadata extraction |
| `document` | 8 | PDF, DOCX metadata |
| `photo` | 8 | Image metadata (supplement to ExifInfo) |

---

## Implementation Approach

**Preferred (local extraction):** Replicate Metadata2Go's capabilities using:
- `ExifTool` for image/video EXIF metadata
- `iTextSharp` / `PdfPig` for PDF metadata
- `DocumentFormat.OpenXml` for DOCX/XLSX metadata
- HTML meta tag parsing for URLs (see OpenGraph and MetaTags plugins)

**Fallback (web scraping):** POST a file URL to Metadata2Go's web form and
parse the returned HTML table. This is fragile and not recommended.

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `extraction_mode` | Extraction Mode | Dropdown | No | `local`, `remote` — default: `local` |
| `supported_file_types` | File Types | TextArea | No | Default: pdf docx xlsx jpg png mp4 |

---

## Fields Populated

```
title, creator, created_date, modified_date, software_used,
page_count, word_count, dimensions, file_size, encoding,
metadata_json: { all raw metadata key-value pairs }
```

---

## Rate Limits

- Local mode: no limits
- Remote mode: dependent on Metadata2Go's server — use sparingly

---

## Implementation Notes

- This plugin is primarily useful as a local metadata extraction fallback
  when ExifInfo is not available or for document types it doesn't cover
- For PDF: use `PdfPig` NuGet package to read XMP/Dublin Core metadata
- For DOCX: `DocumentFormat.OpenXml` reads `CoreProperties` (title, creator,
  description, keywords, created, modified, revision)
- Consider merging this functionality into the ExifInfo plugin rather than
  maintaining a separate plugin

---

## Scaffold Location

```
Chronicle.Plugin.Metadata2Go/
├── Chronicle.Plugin.Metadata2Go.csproj
├── README.md (this document)
├── manifest.json
├── Metadata2GoPlugin.cs
└── Models/
    └── DocumentMetadata.cs
```
