# Open Library plugin: design

Status: **proposed, not implemented.** Repo: `Chronicle.Plugin.OpenLibrary` (public, currently a stale scaffold written against an
old interface; it is replaced, not extended). Plugin id `chronicle.plugin.openlibrary`, source name `openlibrary`.

## 1. Purpose and scope

A second metadata source for the `books` and `audiobooks` media types, beside Hardcover. No API key, no account.

Why it is worth building (measured on the live library, 2026-10-10):

* 750 authors / 956 series entries / 2,992 books (books) and 113 / 820 / 2,778 (audiobooks); Hardcover is the only source.
* Hardcover has 1,684 "not found" rows, and a daily request quota that has already run out once. A sample of those rows
  is overwhelmingly well-known titles (Oathbringer, Ubik, Shadows of Self, Pebble in the Sky, Emily Henry, John Green), so
  they are provider gaps, not obscure books.
* A prototype matcher run against the live Open Library on a sample of those rows matched nearly every well-known book and
  author (details in section 8).

In scope: authors (level 0), books (level 2, and level 1 when it is really a standalone book), for both types.
Out of scope, on purpose: series (section 6), narrators and audiobook runtime (Open Library does not record them),
reading-history import, any reliance on scraping HTML.

## 2. What Open Library gives us (verified against the live service)

| Need | Endpoint | Notes |
|------|----------|-------|
| Find a book | `GET /search.json?title=&author=&fields=...&limit=10` | One call returns title, subtitle, authors and author keys, alternate author names, first publish year, cover id, edition count, languages, ratings, ISBNs, Goodreads / LibraryThing / Amazon / Wikidata ids, page count. |
| Full book | `GET /works/{OLID}.json` | Description (string or `{value}`), subjects, covers, structured `series` (only on some works), identifiers. |
| Find an author | `GET /search/authors.json?q=&limit=` | Name, alternate names, birth date, work count, top work. |
| Full author | `GET /authors/{OLID}.json` | Biography (string or `{value}`), photos (may contain the sentinel `-1`), alternate names, birth/death date, remote ids (VIAF, ISNI, Wikidata, Goodreads...), links. |
| Cover / photo | `covers.openlibrary.org/b/id/{id}-L.jpg`, `/a/id/{id}-L.jpg` | Not rate limited by id. `?default=false` gives a 404 instead of a blank image. |
| ISBN | `GET /isbn/{isbn}.json` | 302 to the edition; the edition lists its work. |

Usage policy (from openlibrary.org/developers/api): 1 request/second by default; **3/second** if requests carry a
`User-Agent` naming the application plus a contact email or phone; do not scrape HTML; do not make hundreds of
single-book requests where `search.json` can batch; do not crawl the covers API; cache responses.

Findings that shape the design:

1. **Series data is sparse.** Of 22 sampled series from the library, one resolved to an Open Library series; Oathbringer, Harry
   Potter, Foundation and Murderbot have none. There is no series search endpoint.
2. **Data quality is uneven.** Search returns study guides, "Summary of...", box sets and anthologies as separate works, and
   the same title by different authors.
3. Descriptions and biographies are **Markdown-ish** (`**bold**`, `*italic*`, `[text][1]` footnote references, `\r\n`).
4. Many works have translations; titles in the library are sometimes translations (Greek, French).
5. A pen name can be its own author record or an alternate name; the same person can exist as two author records.

## 3. Identity and IDs

* ExternalIds are bare Open Library ids, whose suffix says what they are: `OL16114008W` (work), `OL6982995A` (author).
  (Hardcover prefixes `hardcover:author:` etc.; Open Library's own ids are already unambiguous, and cross-reference seeding by
  the host produces `openlibrary:OL...W`, so the parser accepts the bare id, `openlibrary:` + id, `/works/OL...W`, `/authors/OL...A`
  paths and openlibrary.org URLs.)
* **Fix match** accepts: an OLID, an openlibrary.org URL (work or author), an ISBN-10/13, `goodreads:<id>`.
* `GetAcceptedCrossRefPrefixes()` = `openlibrary:`.
* ExtendedData `ids` (read by the host to seed other plugins; strings only, single value each): `goodreads`, `librarything`,
  `amazon` (ASIN), `isbn13`. **Not** put under `ids`: Open Library's `musicbrainz` / `bookbrainz` / `wikidata` / `viaf` / `isni`
  values. A `musicbrainz` key would seed Chronicle's MusicBrainz plugin with a *book's* MBID. Those go under
  `extendedData.external_ids` where nothing acts on them (lossless, inert).
* Host rule to live with: an external id can belong to one Chronicle item library-wide, so a book present in both `books` and
  `audiobooks` can attach its Open Library id to only one of them (already true for Hardcover ids; the second is logged as a
  likely duplicate).

## 4. Types, levels and priority

* Declares `books` and `audiobooks`, each `HierarchyLevels = 3`, labels `Author, Series, Book` (identical to Hardcover's
  declaration), so type creation stays consistent. Audiobooks use the same lookup (Open Library has one work per book,
  whatever the format).
* `DefaultPriority = 20` (lower number wins; Hardcover is 10). Open Library fills what Hardcover did not or could not;
  Metadata Assignment can reorder it per field.
* No `LevelFields` emptied: level 1 also holds standalone books in this library, so level 1 cannot be declared unsupported.

## 5. Matching

All text comparison uses one normaliser: fold accents, lower-case, drop a leading article, drop everything that is not a
letter or digit (so "Commander-in-Chief" = "Commander in Chief", "J. R. R." = "JRR"). Author names also fold "Last, First".
Item names lose a trailing "(YYYY)" before use (the library has them: "Ubik (1969)").

### 5.1 Author (level 0)

1. Known `openlibrary` id -> fetch (score 100, `IdentifierMatch`).
2. `search/authors.json?q=<name>`; keep candidates whose name or an alternate name equals the item's name after normalising.
3. Score: 60 base, plus up to 15 for work count (more works = the well-known one: John Green 381 works beats 43); birth year
   (in this library an author's "year" is the birth year) agrees within 1: +20, disagrees: -30.
4. If two or more candidates remain close and the context supplies children, confirm by asking whether the candidate's works
   include a child title (at most 2 extra `search.json` calls, only in the ambiguous case): confirmed +25.
5. No candidate with an equal name -> no result. (Never match on a similar name.)

### 5.2 Book (level 2, or level 1 with no children)

1. Known `openlibrary` id -> fetch (100). Known Goodreads id -> `search.json?q=id_goodreads:` (identifier match).
2. Query by **main title** plus the author (from the parent or grandparent name); if that finds nothing, the full title; if
   still nothing and an author was given, the title alone. The main title is the text before the first `:` or ` - `, with a
   trailing series-position marker removed (`, Part 2`, `Book Three`, `Volume 4`, `Vol. 4`, `#59`). The prototype showed full
   titles with long subtitles break the search ("Atomic habits: An Easy & Proven Way..." found only junk), and
   "A Dance with Dragons, Part 1: Dreams and Dust" found nothing until the marker was removed.
3. Score each document:
   * title equal after normalising 60; main titles equal 50; one contains the other (min length 6) 30; otherwise discard.
   * author: any author name or alternate author name equals the item's author +30. **Known author that does not match: the
     score is capped at 35** (below the host's 50 gate): the prototype produced "The Outpost" by Mike Resnick for Rick
     Partlow's book at 44 without this.
   * junk: a result titled like a study guide / summary / box set / omnibus / workbook / companion / anthology / collection
     is -40 unless the item's own title says so.
   * language: if the document lists languages and not the preferred one, -10 (not applied to non-ASCII item titles).
   * first publish year within 1: +8 (small: audiobook and reissue years differ from first publication).
   * edition count: up to +8 (popularity tiebreak between duplicate works; Second Foundation 81 editions beats the 2-edition twin).
4. Return the top five; the host applies its own confidence threshold (50 by default).
5. Only the top candidate costs a second request (`works/{OLID}.json` for description and series).

### 5.3 Series (level 1 with children)

**No lookup, no network calls.** The result is empty immediately. Reason: 1 of 22 sampled series resolved, and each attempt
would cost 3-4 requests, about 45-60 minutes of API time per full pass for roughly 5% yield. Where a *book* has Open Library
series data (`series` structure, or a `series:Name` subject), it is recorded on that book (section 7), so the information is not lost.

## 6. Field mapping

| Chronicle field | Source |
|-----------------|--------|
| Title | work title (author: name) |
| AlternateNames | `Title: Subtitle` and `Title (Subtitle)` (so the host's name-overlap gate accepts items named with a subtitle), alternate titles; for authors `alternate_names` |
| Overview | description / biography, Markdown stripped, footnote references and `Source:` trailers removed, paragraphs kept |
| Year | first publish year (author: none) |
| PosterUrl | `covers.openlibrary.org/b/id/{cover_i}-L.jpg`; authors: first positive photo id (`-1` skipped) |
| Rating | Open Library's native 0-5 average (same scale Hardcover already stores for these types); count in extendedData |
| Genres | BISAC-style subject headings ("Fiction, science fiction, general" -> "Science Fiction") |
| Tags | remaining subjects (administrative noise such as "accessible book", "protected daisy", "in library", "overdrive", "nyt:" removed); full original list kept in extendedData |
| Cast | authors as `CastMember(name, "Author", OLID, photo)` |
| ExtendedData | `ids` (section 3), `external_ids`, `series {name, key, position}`, `pages`, `edition_count`, `languages`, `first_publish_year`, `isbn` (first 30, count recorded), `ratings_count`, birth/death date and `links` for authors |

Nothing is invented: no poster for a series, no narrator, no runtime.

## 7. Settings

| Key | Default | Meaning |
|-----|---------|---------|
| `contact_email` | empty | Optional. Put in the User-Agent so Open Library allows 3 requests/second and can contact you if volume is a problem. Not stored anywhere else, never hard-coded. |
| `preferred_language` | `eng` | ISO 639-2 code used only as a tiebreak. |

User-Agent: `Chronicle-OpenLibrary/<version> (<email>)` with an email, `Chronicle-OpenLibrary/<version>` without.

## 8. Rate limiting, errors, caching

* Pace: 1.1 s between requests without an email, 0.4 s with one. One shared limiter per plugin instance (Chronicle gives a
  plugin 25 seconds per call, so a search must stay at 4 requests or fewer; the design needs 2-3).
* HTTP 429 (or 403 from the covers/limiter) is **thrown as `HttpRequestException` with status 429**, not waited out: Chronicle
  then leaves the item Pending and stops the pass instead of marking it not found. (AniList taught this: waiting out a long
  rate limit past the 25 s ceiling turned every search into "not found".) A `Retry-After` of 8 seconds or less is waited once.
* 5xx and network errors propagate as errors (Chronicle retries transient ones twice itself).
* A 404 on a work or author is a normal "not found", not an error.
* Ten-minute in-memory cache of fetched authors and works (the same author is needed for every book under it).
* Never scrape HTML; never download covers in bulk (URLs only; the app fetches an image only when asked).

Budget for the first full pass over this library: roughly 750 authors x 2.3 + 3,900 books x 2.3, about 10,700 requests,
**about 3 hours at 1/s, about 1 hour at 3/s**. Series cost nothing. The weekly re-sync task is off by default.

## 9. Background tasks

`fetch-missing-metadata` (daily, enabled) and `resync-all-metadata` (weekly, disabled), using Chronicle's built-in handlers,
like TVMaze and AniList. Cron times offset from other plugins.

## 10. Evidence from the prototype

Sample: 92 library items (half Hardcover "not found", half matched, split across levels). For the "not found" half:

* Authors: 7 of 8 matched (the eighth, R.A. Williamson, does not exist in Open Library). Two or more same-name records for
  Fiona Staples, Emily Henry, John Green, Becky Chambers: the work-count and birth-year rules pick the real one.
* Books: nearly all well-known titles matched at 86-104. Correct rejections: The Venom Business (credited to Michael
  Crichton in Open Library, the item says pen name John Lange), The Outpost and Master of the Revels (same title, different
  author), Blood Lite III (anthology by another editor), Greek and French translations (no title match), indie titles absent
  from Open Library.
* Control half (items Hardcover did match): the same work is found for nearly every well-known book (The Eye of the World, Nemesis,
  Judgment at Proteus, The Scarab Path, The Lord of the Rings, The Fires of Heaven) and for all 12 sampled authors. Correct
  rejections: "City World" (different book), comics issues ("Invincible #59") and translations. Co-authored books (Saga, with Fiona
  Staples and Brian K. Vaughan) match through either author.
* The prototype's problems that this design fixes: the long-subtitle query, the series-position suffix, the wrong-author floor, junk "Summary of..."
  works, and the "&" vs "and" difference.

## 11. Tests

* Unit, with recorded real responses as fixtures: normaliser and name rules, year-suffix stripping, title forms, junk
  detection, author scoring (same-name authors, birth year), book scoring (every prototype case above as a regression
  test), Markdown cleaning, id/URL/ISBN parsing, subject to genre/tag split, cover and photo URLs (`-1` skipped).
* Contract tests with a fake HTTP handler: request budget per call (2-3, never more than 4), 429 is thrown not waited out,
  404 is empty, pacing with and without an email, User-Agent contents, the cache stops repeat fetches, series items make zero requests.
* Live tests, off unless `OPENLIBRARY_LIVE=1`: the library's real titles and authors, rate limit respected.
* Manifest test: id, version, tasks, supported types match the code.

## 12. Release and docs

README in the standard plugin format (badge, Plugin ID / Version / Auth block, sections), `plugins.json` entry with tags
`books`, `audiobooks`, `metadata`, a GitHub release with one zip (DLL, deps.json, manifest), added to `RunTestEnvironment.ps1`.
No host (Chronicle) code changes are needed.

## 13. Decisions needed

See the summary in the conversation: contact email setting, series handling, rating scale, priority, genres, scope of audiobooks.

## 14. Risks

* Open Library outages or slowness (it runs on donated infrastructure): errors propagate, items stay Pending.
* Wrong-work matches for books whose Open Library record is poor; mitigated by the author floor and junk penalty, and by the
  host's confidence gate; a user can correct any match with Fix match.
* The first full pass is slow by design.
* Policy changes at Open Library (limits, required headers): pacing and User-Agent are centralised in one client class.
