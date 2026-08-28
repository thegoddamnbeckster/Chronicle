# Chronicle Plugin Specification: Wikipedia (V3)

This document provides a complete implementation specification for the Wikipedia metadata plugin for Chronicle. Wikipedia serves as a broad fallback metadata provider for any media type (Movies, TV, Music, Books, Games) that can be resolved to an article.

---

## Section 1 — Service Overview
**Service Name:** Wikipedia  
**Website:** [https://wikipedia.org](https://wikipedia.org)

Wikipedia is a free, multilingual online encyclopedia. It provides high-quality summaries and basic metadata for almost any significant media property.

**Chronicle Media Types Supported:**
- `movie`, `tv`, `music_artist`, `music_album`, `book`, `video_game`, `podcast`, `audiobook`
- **Note:** This plugin should be available as a metadata option for **ANY** media type.

---

## Section 2 — Authentication & Credential Acquisition
**Mechanism:** None / User-Agent based.
Wikipedia's API does not require an API key for public read access. However, the [Wikimedia Foundation User-Agent Policy](https://meta.wikimedia.org/wiki/User-Agent_policy) requires a descriptive User-Agent header.

**Required Header:**
- `User-Agent: Chronicle/1.0 (https://github.com/your-repo; your-email@example.com)`

---

## Section 3 — Plugin Settings Schema
| Key | Label | Description | Type | Required | Default |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `language` | Language Code | The Wikipedia language subdomain to search (e.g., 'en', 'fr', 'de'). | String | True | "en" |

---

## Section 4 — Manifest Values
**`manifest.json`**
```json
{
  "plugin_id": "chronicle.plugin.wikipedia",
  "name": "Wikipedia",
  "version": "1.0.1",
  "author": "Chronicle Contributors",
  "description": "General purpose metadata and summaries from Wikipedia with normalized links.",
  "min_chronicle_version": "1.0.0",
  "entry_type": "Chronicle.Plugin.Wikipedia.WikipediaMetadataProvider",
  "iconUrl": "https://wikipedia.org/static/apple-touch/wikipedia.png",
  "brandColorLight": "#FFFFFF",
  "brandColorDark": "#000000",
  "fixMatchHint": "Enter the Wikipedia Page Title or full URL.",
  "supported_media_types": ["*"]
}
```

---

## Section 5 — Search Endpoint Specification
### Endpoint: Action API Search
- **HTTP Method:** `GET`
- **URL Template:** `https://{lang}.wikipedia.org/w/api.php?action=query&list=search&srsearch={query}&format=json&origin=*`

**Example Request:**
`GET https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch=The+Batman+film&format=json`

---

## Section 6 — Fetch-by-ID Endpoint Specification
### Endpoint: Page Summary REST API
- **URL Template:** `https://{lang}.wikipedia.org/api/rest_v1/page/summary/{title}`

**Example Response (Excerpt):**
```json
{
  "title": "The Batman (film)",
  "extract": "The Batman is a 2022 American superhero film...",
  "extract_html": "<p><b>The Batman</b> is a 2022 American superhero film based on the <a href=\"/wiki/DC_Comics\">DC Comics</a> character Batman.</p>",
  "originalimage": {
    "source": "https://upload.wikimedia.org/wikipedia/en/f/f7/The_Batman_poster.jpg"
  }
}
```

---

## Section 7 — Field Mapping Table
| MediaMetadata Field | API Response Path | Notes / Transformation |
| :--- | :--- | :--- |
| `ExternalId` | `wikipedia:{lang}:{title}` | |
| `Source` | `"wikipedia"` | |
| `Title` | `title` | |
| `Overview` | `extract_html` | **CRITICAL:** Must apply link normalization (See Section 14). |
| `PosterUrl` | `originalimage.source` | Fallback to `thumbnail.source`. |
| `ExtendedData` | `wikibase_item` | Stores the Wikidata ID. |

---

## Section 8 — ExternalId Convention
- **Format:** `wikipedia:{lang}:{page_title}`
- **Parsing:** Split by `:` to get the language subdomain and the title.

---

## Section 9 — Image Handling
- **Primary Image:** Use `originalimage.source`.
- **Thumbnail:** Use `thumbnail.source`.

---

## Section 10 — Rate Limiting
- **Limit:** Safe up to 100 requests per second with a User-Agent.

---

## Section 11 — Scoring Strategy
1. **Exact Title Match (including disambiguation):** +60 points.
2. **Title Match (ignoring disambiguation):** +40 points.
- **Min Threshold:** 50.

---

## Section 12 — MediaTypeSupport
| MediaTypeName | SupportedFields | Priority |
| :--- | :--- | :--- |
| `*` | `title, overview, poster` | 5 |

---

## Section 13 — Edge Cases
- **Disambiguation Pages:** Look for "(film)", "(TV series)", etc., in titles.

---

## Section 14 — Text Post-Processing & Link Normalization
When retrieving `extract_html` for the `Overview` field, the plugin MUST perform the following transformations on all `<a>` tags:

1.  **Relative Link Normalization:**
    - Any link starting with `/wiki/` must be converted to a full absolute URL.
    - **Formula:** `https://{lang}.wikipedia.org/wiki/{Page_Title}`
    - **Attribute Requirement:** The resulting `<a>` tag must include `target="_blank"` to ensure it opens in a new page.
    - **Example:** `<a href="/wiki/DC_Comics">` becomes `<a href="https://en.wikipedia.org/wiki/DC_Comics" target="_blank">`.

2.  **Unresolvable Links:**
    - If a link does not start with `/wiki/` or cannot be reliably converted to a Wikipedia URL, the link (`<a>` tag) must be **stripped**, leaving only the inner text.
    - **Example:** `<a href="#References">Note</a>` becomes `Note`.

3.  **Clean Up:**
    - Ensure no relative internal references (e.g., `#cite_note-1`) remain as clickable links.
