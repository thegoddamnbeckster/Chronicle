# Feature Design: Book Import Plugins (Hardcover + Goodreads)

**Status:** Design/Planning
**Target:** Phase 3
**Goal:** Import reading history, ratings, and want-to-read lists from book tracking services into Chronicle. Two plugins: one for Hardcover.app (active GraphQL API) and one for Goodreads (RSS/CSV export, as their API is deprecated).

---

## Hardcover.app Plugin (`Chronicle.Plugin.Hardcover`)

### Overview

Hardcover uses a **GraphQL API** with Bearer token authentication. API keys are available from each user's account settings page. No OAuth device flow is required — users paste their API key directly into Chronicle.

### Authentication

Simple API key — no OAuth flow needed:
- User visits `https://hardcover.app/account/api` → copies their personal API token
- Pastes it into Chronicle → Plugins → Hardcover → Settings → API Token
- Token is passed as `Authorization: Bearer {token}` on every request

### Hardcover API Details

- **Endpoint:** `https://api.hardcover.app/v1/graphql`
- **Method:** All requests are `POST` with a JSON body containing `{ "query": "...", "variables": {...} }`
- **Rate limit:** ~100 requests/minute (documented); respect `429` responses

### Data Available via GraphQL

```graphql
# Reading history (books with status "read")
query GetReadBooks {
  user_books(where: { status_id: { _eq: 3 } }) {
    book { title, release_year, contributions { author { name } } }
    rating
    date_read: user_book_reads(order_by: { finished_at: desc }, limit: 1) {
      finished_at
      started_at
    }
  }
}

# Want to read list (status_id = 1)
query GetWantToRead {
  user_books(where: { status_id: { _eq: 1 } }) {
    book { title, release_year }
    added_to_list_at: inserted_at
  }
}
```

Hardcover book status IDs:
- `1` = Want to Read
- `2` = Currently Reading
- `3` = Read
- `4` = Did Not Finish

### Chronicle MediaType Mapping

Chronicle must have a `"book"` media type configured. The import maps:
- Status `3` (Read) → `LibraryStatus.Completed`
- Status `2` (Currently Reading) → `LibraryStatus.Watching`
- Status `1` (Want to Read) → `LibraryStatus.PlanToWatch`
- Status `4` (DNF) → `LibraryStatus.Dropped`

### External IDs

Hardcover provides cross-references to:
- `hardcover:{id}` — primary
- `isbn_13`, `isbn_10` — book ISBNs
- `openlibrary_id` — Open Library identifier

### Plugin Structure

```
W:\Scripts\Chronicle.Plugin.Hardcover\
  Chronicle.Plugin.Hardcover.csproj
  manifest.json
  HardcoverModels.cs       ← GraphQL response records
  HardcoverClient.cs       ← HTTP wrapper for GraphQL endpoint
  HardcoverImportProvider.cs ← IImportProvider implementation
```

### Settings Schema

| Key | Type | Description |
|---|---|---|
| `api_token` | Password | Hardcover personal API token |

### Auth Flow

Since there is no device/PIN flow, `StartAuthAsync` and `PollAuthAsync` throw `NotSupportedException`. Chronicle's UI should detect this (via a capability flag) and show a simple "paste your API token here" form instead of the device-code flow.

Add a capability check to `IImportProvider`:
```csharp
ImportCapabilities GetCapabilities();
// Extend ImportCapabilities:
public record ImportCapabilities(
    bool SupportsHistory,
    bool SupportsRatings,
    bool SupportsWatchlist,
    bool RequiresDeviceAuth  // false → show API key field, skip device flow
);
```

---

## Goodreads Plugin (`Chronicle.Plugin.Goodreads`)

### Overview

Goodreads **shut down their public API in December 2020** — no new API keys are issued and existing keys no longer work for most endpoints. However, Goodreads still provides two machine-readable data sources:

1. **RSS feeds** for public shelves (read/currently-reading/want-to-read)
2. **CSV export** from account settings (manual, user-initiated)

The plugin supports both, giving users two import pathways.

### Option A: RSS Feed Import

Every Goodreads user has a public RSS feed per shelf:
```
https://www.goodreads.com/review/list_rss/{userId}?shelf={shelf_name}
```

Example:
```
https://www.goodreads.com/review/list_rss/12345678?shelf=read
https://www.goodreads.com/review/list_rss/12345678?shelf=currently-reading
https://www.goodreads.com/review/list_rss/12345678?shelf=to-read
```

The RSS feed provides: title, author, rating, date_added, date_read, ISBN, average rating, description.

**Limitations:**
- Only works for public shelves
- Pages to 200 items; use `?page=N` to paginate
- No OAuth required — just the Goodreads user ID (a number from their profile URL)

**Plugin settings for RSS mode:**
| Key | Type | Description |
|---|---|---|
| `goodreads_user_id` | Text | Goodreads user ID (from profile URL) |

### Option B: CSV Export Import

Users can export all their data from `https://www.goodreads.com/review/import`.

The exported CSV contains columns:
```
Book Id, Title, Author, Author l-f, Additional Authors, ISBN, ISBN13, My Rating,
Average Rating, Publisher, Binding, Number of Pages, Year Published, Original Publication Year,
Date Read, Date Added, Bookshelves, Bookshelves with positions, Exclusive Shelf,
My Review, Spoiler, Private Notes, Read Count, Owned Copies
```

**Plugin settings for CSV mode:**
| Key | Type | Description |
|---|---|---|
| `csv_file_path` | FilePath | Path to the exported Goodreads CSV file |

Chronicle parses the CSV using `CsvHelper` (NuGet) and maps:
- `Exclusive Shelf = read` → `LibraryStatus.Completed`
- `Exclusive Shelf = currently-reading` → `LibraryStatus.Watching`
- `Exclusive Shelf = to-read` → `LibraryStatus.PlanToWatch`
- `My Rating > 0` → import as rating (Goodreads uses 1–5 scale; multiply by 2 for Chronicle's 1–10)

### Plugin Structure

```
W:\Scripts\Chronicle.Plugin.Goodreads\
  Chronicle.Plugin.Goodreads.csproj
  manifest.json
  GoodreadsModels.cs         ← RSS item + CSV row records
  GoodreadsRssClient.cs      ← RSS feed fetcher + XML parser
  GoodreadsCsvImporter.cs    ← CSV parser using CsvHelper
  GoodreadsImportProvider.cs ← IImportProvider implementation
```

### GetSettingsSchema()

```csharp
// Mode selector drives which fields are visible
new SettingDefinition { Key = "import_mode", Label = "Import Mode",
    Type = SettingType.Dropdown, DefaultValue = "rss",
    Options = [
        new SelectOption { Value = "rss", Label = "RSS Feeds (public shelves)" },
        new SelectOption { Value = "csv", Label = "CSV Export (all shelves)" },
    ]},
new SettingDefinition { Key = "goodreads_user_id", Label = "Goodreads User ID",
    Type = SettingType.Text, Required = false,
    Description = "Number from your profile URL. Required for RSS mode." },
new SettingDefinition { Key = "csv_file_path", Label = "CSV File Path",
    Type = SettingType.FilePath, Required = false,
    Description = "Path to your Goodreads export CSV. Required for CSV mode." },
```

### External IDs

| Source | Key | Format |
|---|---|---|
| Goodreads | `goodreads` | `{book_id}` |
| ISBN-13 | `isbn13` | 13-digit string |
| ISBN-10 | `isbn` | 10-digit string |

---

## Shared: Open Library Metadata Enrichment

Both plugins benefit from Open Library as a free metadata source for books:
- **API:** `https://openlibrary.org/api/books?bibkeys=ISBN:{isbn}&format=json&jscmd=data`
- No authentication required
- Returns: title, authors, subjects, cover images, publish date
- Rate limit: ~100 requests/minute (be polite, cache aggressively)

If a book doesn't already exist in Chronicle's `media_items`, the import service calls Open Library to enrich the stub item with cover art and description.

---

## Chronicle Prerequisites

Before book imports can work, Chronicle needs:
1. A `"book"` entry in `media_types` table
2. The `media_types` record should have appropriate `interaction_verb` (e.g. "Read") and `progress_unit` (e.g. "Pages" or "Percent")
3. Optionally: a Book-specific metadata provider plugin (MusicBrainz-style, using Open Library or Google Books API)

---

## Implementation Order

### Hardcover
1. Create `Chronicle.Plugin.Hardcover` project
2. `HardcoverClient.cs` — GraphQL HTTP wrapper
3. `HardcoverModels.cs` — response records
4. `HardcoverImportProvider.cs` — `IImportProvider` (no device auth, API key only)
5. Extend `ImportCapabilities` with `RequiresDeviceAuth` flag
6. Update `ImportController` to handle API-key-only plugins
7. Git init, push to `thegoddamnbeckster/Chronicle.Plugin.Hardcover`

### Goodreads
1. Create `Chronicle.Plugin.Goodreads` project
2. `GoodreadsRssClient.cs` — XML RSS parser
3. `GoodreadsCsvImporter.cs` — CsvHelper-based CSV parser
4. `GoodreadsImportProvider.cs` — mode-switch between RSS/CSV
5. Git init, push to `thegoddamnbeckster/Chronicle.Plugin.Goodreads`
