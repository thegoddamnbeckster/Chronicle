# Plugin-Declared Metadata Fields — Design

**Date:** 2026-10-01
**Status:** Design, not started
**Goal:** Whatever a provider gives Chronicle becomes a real Chronicle field: stored,
subject to the user's precedence settings, visible, and (where it makes sense) sortable and
filterable. Adding a provider with new kinds of data never requires editing a hardcoded list in
core, and never leaves data stranded.

Related: `2026-10-01-ratings-tracking-design.md` (scores are one family of fields) and
`docs/plugins/PLUGIN_IMDB.md` (first plugin to need this).

---

## 1. The gap today

- The set of fields that take part in Metadata Assignment is **hardcoded**:
  `MetadataResolutionService.FieldMap`, 34 entries. Admins can add *alias names* for those
  fields (`metadata_field_aliases.config`), but not new fields.
- `ResolveAsync` only walks `FieldMap`. Anything else a plugin sends (e.g. TMDB's
  `belongsToCollection` before it was added, Hardcover's series data, IMDb's alternate titles)
  is stored in that plugin's `metadata_json` partition (the lossless-ingestion rule holds) but:
  - it never goes through the user's precedence settings,
  - it never reaches `_resolved`, so the API, the detail page, Kodi and the library can't use it,
  - nothing tells anyone it's there.

Stored-but-unreachable data counts as lost for every practical purpose. This design closes that gap.

---

## 2. Field registry

A DB-backed registry of canonical fields replaces the hardcoded `FieldMap` as the source of
truth. Today's 34 fields become its seed data, so nothing changes for existing data.

**Table `metadata_fields`:**

| Column | Notes |
|--------|-------|
| `Key` (PK) | snake_case, e.g. `original_title`. Same vocabulary Metadata Assignment already uses |
| `DisplayName` | "Original Title" |
| `ValueType` | `text`, `text_list`, `integer`, `number`, `boolean`, `date`, `partial_date`, `url`, `image_url`, `person_list`, `title_ref_list`, `structured` |
| `BlobKeys` | JSON array of property names to look for in a partition, including dotted paths into `extendedData` (e.g. `["originalTitle", "extendedData.originalTitle"]`). Admin alias overrides still layer on top |
| `Group` | Display section on the detail page: `Titles`, `Release`, `Classification`, `People`, `Production`, `Ratings`, … |
| `Sortable` / `Filterable` | Whether the library offers it (scalar types only) |
| `Origin` | `core` or the plugin id that first declared it |
| `CreatedAt` | |

**Who adds fields:**

1. **Core seed:** today's `FieldMap`, inserted by a migration.
2. **Plugins:** a new optional `metadata_fields` array in `manifest.json`:

   ```json
   "metadata_fields": [
     { "key": "original_title", "display_name": "Original Title", "value_type": "text",
       "blob_keys": ["originalTitle"], "group": "Titles", "sortable": true, "filterable": true }
   ]
   ```

   Chronicle upserts these when the plugin loads, the same way `MediaTypeSupport.DisplayName`
   already upserts media types. When two plugins declare the same key, it's **one field**, and
   precedence chooses between them. The first declaration's display name and type stick; a
   later conflicting `value_type` is logged and ignored rather than silently changing stored
   meaning.
3. **Admins:** Settings → Metadata Fields can add a field by hand (e.g. to promote something
   from the "unmapped data" list in §5) without a plugin release.

`MediaTypeSupport.SupportedFields` keeps its role of saying *which* plugin offers *which* field
for *which* type/level. It just may now name registry fields beyond the old 34.

---

## 3. Precedence applies to every field

- `ResolveAsync` iterates the registry instead of `FieldMap`. Each field resolves exactly as the
  existing ones do: `_overrides` pin first, then the user's configured plugin order for that
  type/level, then the existing unconfigured fallback.
- **Metadata Assignment lists every registry field** a plugin supports for that type/level, so
  a new field shows up there automatically, ready for the user to order.
- **Default ordering for a new field** (before the user touches it): the plugin that declared it
  first, then others alphabetically. This is a starting point only; the user's saved order
  always wins.
- Pins (`_overrides`) work for any field, not just artwork.

---

## 4. Exposure

- **API:** `_resolved` carries every registry field. `GET /api/v1/media/{id}` returns them with
  `{ key, displayName, valueType, group, value, sourcePluginId }`, and the source plugin is shown
  so precedence decisions are visible.
- **Detail page:** a generic "Details" area renders resolved fields grouped by `Group` and
  formatted by `ValueType` (dates, lists, links, title references that link to library items).
  Today's dedicated UI keeps working; the generic area shows whatever has no dedicated UI.
- **Library:** fields marked `Sortable`/`Filterable` appear as sort and filter options. These
  values are projected into an index table
  `media_field_values (MediaItemId, FieldKey, TextValue, NumberValue, DateValue)`, written in
  `ResolveAsync` the same way `media_ratings` is, so queries don't parse JSON.
- **Kodi:** the scraper API maps registry fields to Kodi info labels where a field declares a
  `kodi_label` (e.g. `original_title` → `originaltitle`).

---

## 5. Safety net: "unmapped data"

After each enrichment, Chronicle compares the keys present in the plugin's partition (top
level and `extendedData`) with the registry's `BlobKeys`. Keys no field claims are recorded per
plugin (key, sample value, item count) and listed in **Settings → Metadata Fields → Unmapped
data**. From there an admin can create a field for one or alias it to an existing field.

So even when a plugin author forgets to declare something, it is visible and one click away
from becoming a real field, instead of quietly sitting in a JSON blob forever.

---

## 6. Supporting core changes

| Change | Why |
|--------|-----|
| `partial_date` value type + `BirthDatePrecision` / `DeathDatePrecision` (`day`, `month`, `year`) on `media_items` | IMDb (and Wikidata) give birth/death **years**. Storing 1964 as `1964-01-01` invents a day. Precision makes "1964" a first-class value, and precedence can still prefer a provider with a full date |
| `MediaMetadata.Fields` (`Dictionary<string, JsonElement>`) | Lets a plugin send a declared field without core adding a typed property for it. Typed properties (`Title`, `Year`, …) keep working |
| `MediaCredit`: allow several characters per person | IMDb lists e.g. `["Neo", "Thomas A. Anderson"]`. Store one credit row per character with the same billing order, so nothing is joined into a lossy string |
| Credit roles `Self`, `Archive Footage` | IMDb distinguishes documentary/self appearances and archive footage from acting. Keep them as roles rather than flattening to "Actor" |
| `SettingType.Notice` | A read-only settings callout (`info` / `warning`) for things a user should know before enabling a plugin, e.g. disk use. Generic; any plugin can use it |

---

## 7. Build order

1. `metadata_fields` table seeded from `FieldMap`; `ResolveAsync` reads the registry (no
   behaviour change, verified by resolving the whole library before/after and diffing `_resolved`).
2. Manifest `metadata_fields` + load-time upsert; Metadata Assignment lists registry fields.
3. API exposure + generic Details area.
4. Unmapped-data detection + Settings → Metadata Fields page.
5. `media_field_values` projection + library sort/filter.
6. `partial_date`/precision, multi-character credits, new roles, `SettingType.Notice`.
