# LibraryThing plugin: investigation (2026-11-18)

**Status: NOT TO BE DEVELOPED (decision 2026-11-19).** Nothing is built. The owner tried to request API access on the Developer Hub
(`librarything.com/developer`) and the page offers no way to request it, so no key can be obtained and access from the Chronicle
server can never be proven. Reopen only if LibraryThing opens key requests again or grants access when asked directly.

**What LibraryThing requires (so a future reader need not repeat this):**
- A developer key tied to a LibraryThing account, issued through the Developer Hub. As of 2026-11-19 the hub offers no request option.
- Its site and API sit behind Cloudflare bot protection (HTTP 403 "One moment" page to a plain client), so even a key may not be
  enough from a server. Chronicle never bypasses bot checks.
- Terms (old, unverified today): non-commercial use, one request per second, about 1,000 requests a day, access at LibraryThing's
  discretion; Common Knowledge data is CC BY.

The original investigation follows, unchanged.

## What prompted it

The person page for Andy Weir shows `Librarything: weirandy` and `Goodreads: 6540057` under Extended Data. Both come from Open Library's
author record (`remote_ids`), which the Open Library plugin keeps (lossless ingestion). Chronicle uses neither today. `weirandy` is
the author's name on `librarything.com/author/weirandy`. Open Library's search also returns `id_librarything` for books (a numeric
work id), which the Open Library plugin already stores under `librarything`.

## What LibraryThing offers (from public documentation; most sources are old and I could not read the official pages)

I could not open LibraryThing's own documentation: every request from this machine, including the keyless REST endpoint itself
(`/services/rest/1.1/?method=librarything.ck.getwork&isbn=...`), returned HTTP 403 with a Cloudflare "One moment" block page
(2026-11-18, two requests, identifying itself as a research client). That is bot protection and I did not try to get past it. So
everything below is from third-party and archived sources and **must be re-checked against the Developer Hub with a real key**.

| API | What it returns | Key | Licence tier (as documented) |
|---|---|---|---|
| **thingISBN** | every ISBN LibraryThing treats as another edition of the same work | not needed | non-commercial |
| **thingTitle** | titles for an ISBN | not needed | non-commercial |
| **Web Services, Common Knowledge** (`librarything.ck.getwork`, `getauthor`) | the community-edited "Common Knowledge" data: series, characters, places, awards, author details such as education | a free developer key | the data is Creative Commons Attribution; use of the API is non-commercial unless LibraryThing agrees |
| **Recommendations** | books LibraryThing members' libraries suggest | key | the documentation says free use is limited to 25 queries a day |
| **Covers** (ISBN cover service) | cover images by ISBN | key | historically 1,000 per key per day |

**Terms that applied in the sources I could find** (older than 2014 in most cases, so treat as unverified today):
non-commercial use unless written permission is given; one request per second; about 1,000 requests a day per key, above which
LibraryThing wants to be contacted; access is at LibraryThing's sole discretion and can be withdrawn at any time; commercial use needs
a visible link back. Chronicle is a self-hosted personal tracker, which is non-commercial use.

Not found: any change to these terms in 2024 or 2025, and the current method reference with exact response fields.

## Blocker

LibraryThing sits behind Cloudflare bot protection, and it blocked a plain HTTP client on both its documentation and its API
endpoint. A Chronicle plugin is exactly such a client. Chronicle's rule for plugins is that they respect a provider's bot checks and
never bypass them (as with ListenBrainz and Wikimedia). Whether a request carrying a valid developer key and a descriptive
`User-Agent` is let through, or whether this machine's address was simply blocked, **cannot be known without a key.** If it is blocked,
the only honest routes are to ask LibraryThing to allow the plugin's access (through the Developer Hub) or to not build it.

## What it would add, compared with what Chronicle already has

| Data | Already from | LibraryThing would add |
|---|---|---|
| Title, description, cover, ISBNs, subjects | Open Library, Hardcover | little |
| Rating | Hardcover, Open Library | LibraryThing's own average (a third opinion) |
| Series | Hardcover (and Open Library for some) | Common Knowledge series, often more complete for older fiction |
| **Characters, places, awards, first sentence, dedication, epigraph** | **nobody** | **the real gain**: community-edited and CC BY licensed |
| Tags | Hardcover, Open Library subjects | LibraryThing's folksonomy tags |
| Other editions of a work | Open Library | thingISBN (no key) |

The distinctive value is Common Knowledge (characters, places, awards). Everything else is already covered.

## How a plugin would work (sketch, not a commitment)

- `IMetadataProvider` for `books` (and `audiobooks`), supplementary like Open Library: never the first source for a title. Settings:
  the developer key (a `Password`), optional.
- **Identification without searching by name:** read the `librarything` id Open Library already stored on the item (and the author
  slug, `weirandy`), or look the book up by ISBN (`isbn13` from Open Library or Readarr, see the Arr Apps plugin). No name search, which
  is what makes it safe.
- Pacing: one request per second, a daily budget well under 1,000, a bounded retry on 429, and a cache. Declare `EnrichOnly` so it is
  left out of interactive search (it cannot answer in 4 seconds at that pace).
- Every field the source returns is kept (lossless ingestion), partitioned under `chronicle.plugin.librarything`; characters, places
  and awards go in extended data and are shown on the book page.
- Fits the plugin checklist (`docs/PLUGIN_NEW_TYPE_CHECKLIST.md`): S1 (record real responses as fixtures) is **not possible until a key
  is entered**, S2 (what is a community claim) applies to the terms above, S5 (terms and account risk) is this document.

## Risks

1. **Access** (the blocker). It may simply not work from the server.
2. **Terms drift.** The terms I found are old; a key can be withdrawn at any time.
3. **Low-volume, high-value only.** A library of thousands of books at about 1,000 requests a day is days of enrichment; acceptable
   as a background task, not for a bulk first pass.
4. **Account risk.** The key is tied to the owner's LibraryThing account; aggressive use could cost them it.

## Next steps (all the owner's, because they involve an account and a key)

1. Create a LibraryThing account and request a developer key on the Developer Hub (`librarything.com/developer`), and read the terms
   shown there today.
2. From the Chronicle server (not a browser), make one request with the key, for example
   `curl -A "Chronicle/1.0" "https://www.librarything.com/services/rest/1.1/?method=librarything.ck.getwork&isbn=9780553418026&apikey=<key>"`,
   and say what comes back: XML data, an error, or the same "One moment" page.
3. If data comes back: say so, and the plugin is worth building (record that response as the first fixture, then follow the sketch).
   If the block page comes back: do not build; ask LibraryThing through the Developer Hub whether the plugin's access can be allowed.

## Sources

- LibraryThing APIs wiki page (`wiki.librarything.com/index.php/LibraryThing_APIs`), via search results; not readable directly (403).
- LibraryThing API Terms of Use and Developer Hub (`librarything.com/developer/terms`, `librarything.com/developer`), via search results; not readable directly (403).
- LibraryThing blog, "Free web services API to Common Knowledge" (2008) and "A million free covers" (2008).
- The Code4Lib Journal, issue 21 (2013): a review of web APIs for book data.
