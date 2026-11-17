# Chronicle.Plugin.Wikidata

**Plugin ID:** `chronicle.plugin.wikidata`
**Source name:** `wikidata`
**Media types:** movies, TV, music, books, games, people (it never owns a type)
**Auth:** none: no key, no account, no other plugin
**API:** Wikidata Action API, read only: `https://www.wikidata.org/w/api.php`
**Repository:** `thegoddamnbeckster/Chronicle.Plugin.Wikidata`
**Design and run record:** [2026-11-16-wikidata-plugin-design.md](../plans/2026-11-16-wikidata-plugin-design.md)

The first design of this page assumed a SPARQL endpoint and a music-only plugin. Measuring the service showed the Action API is the right
tool (a reverse lookup by another service's id is one cheap request, and the query service lags), so this page replaces it.

## What it does

Three jobs, all by exact id:

1. **Cross-reference hub.** An item found by an id Chronicle already holds (IMDb `tt…`, TMDB movie or TV id, TheTVDB, TVmaze, MusicBrainz
   artist / release group / recording, Steam, IGDB slug, Open Library) publishes the other ids Wikidata knows in `ExtendedData.ids`, which the
   host reads to seed enrichment rows for the other plugins without a text search.
2. **Structured facts.** Names in every language, year, genres, credits (companies with a logo where Wikidata has one), image, logo and
   poster, official websites, sitelinks, and the whole entity raw. For a game also platforms, game modes and engine (as tags) and its
   designers, programmers, creators and art directors (as crew).
3. **People, completely.** Every person in a cast or crew list, every voice actor and every band member is looked up in full, so each credit
   carries the person's portrait when Wikidata has one (D10: nothing is left out to save requests). And a person Chronicle already has is found by IMDb name id or TMDB person id (never by name) and filled in: birth and death date
   (only when known to the day), the portrait, occupations, other-language names. Only the IMDb name id and the TMDB person id are passed
   on for a person.

Automatic matching is by id or absent. The interactive search (Add Media, Fix Match) looks by name and keeps only results whose one-line
description names the right kind of thing (a heuristic used for the interactive list only).

## Ids

`wikidata:Q<number>` always. Accepted: `Q42`, `wikidata:Q42`, `wikidata.org` addresses (https, exact hosts); anything after the number is refused.

## Measured (2026-11-16)

- Reverse lookup by id (`haswbstatement:P1733=553850`) answers in about 0.3 s; **a quoted value matches nothing** (found by the live test).
- One full entity: 127 to 247 KB (Helldivers 2, The Matrix, Radiohead), 0.3 to 0.6 s per request; a whole fetch with references resolved takes 4 to 5 s.
- **Most labels are in the language-neutral `mul` code, not `en`.**
- **Errors arrive as HTTP 200** with an `error` object (`no-such-entity`, `maxlag`).
- **Wikidata has no logo for Arrowhead Game Studios** (and none for most companies); where one exists it is a single image.
- No rate-limit headers and no 429 in the measurement; Wikimedia's policy asks for serial requests and a descriptive User-Agent, so the plugin
  keeps 1.2 s between requests (never under 1 s), shared by everything it does.

## Settings

None required. Optional: a contact detail (sent to Wikimedia only, in the User-Agent) and the request interval (1000 ms or more).

## Tasks

`fetch-missing-metadata` (on, `22,52 * * * *`) and `resync-all-metadata` (off, Sunday 03:52).

## Terms

Wikidata's structured data is CC0. Commons images carry their own licences. A descriptive User-Agent and polite pacing are required and done.

## Not done in v1

TV seasons and episodes by name, a numeric-id lookup for IGDB (Wikidata holds IGDB's slug only), AniList lookups (published only), a game's series as a
collection (Wikidata's "part of the series" is not consistent enough to group by).
