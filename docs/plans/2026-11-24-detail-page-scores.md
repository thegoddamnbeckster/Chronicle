# Scores on the media detail page — plan

**Date:** 2026-11-24
**Status:** Plan written and reviewed (section 9). Not started.
**Request:** On an item's page, in the empty space beside the Status / Rating controls, show every score a plugin has for the
item: the plugin's logo, then its score the way the plugin's own site shows it (IMDb `8.2/10`, TMDB `80%`, a five-star source
`4.2/5 ★`). Any plugin that can supply a score, for any media type.
**Rule from the owner:** use the value as the plugin's site presents it, when that is obvious (4/5 stars, 75% are fine; a bare `4` is
not).

**Answer: yes, it is doable, and most of it is already designed.** `docs/plans/2026-10-01-ratings-tracking-design.md` ("Design, not
started") is the full ratings project: a plugin contract (`ProviderRating`, native scale), a `media_ratings` table, a manifest
`rating_sources` registry, API fields, a detail-page Ratings strip, library sort and filter, Kodi, history. This request is its
section 6 (the strip) plus the smallest slice of sections 2, 4 and 5 that the strip needs. This plan builds that slice and leaves
the rest of the project for later, so nothing built now has to be redone.

---

## 1. What exists today (verified 2026-11-24)

| Fact | Evidence |
|------|----------|
| Every plugin's data sits in the item's `MetadataJson` under its own partition, and the detail API already returns it as `pluginMetadata` | `MediaController.ToDto`; the 1917 page |
| A partition carries a plain `rating` (a number) when the plugin has one | 1917: Simkl 6.5, TMDB 7.984, IMDb 8.2; FanartTV, Wikipedia, Wikidata none |
| IMDb, IGDB (and IMDb season roll-ups) already write the contract list `extendedData.ratings[] = {key, variant?, value, scale, votes?, url?}` | `ImdbMapper.cs:123,253`, `IGDB/MetadataMapper.cs:193-203`; 1917 IMDb: `{key:imdb, value:8.2, scale:10, votes:787753, url}` |
| OMDb writes `ratings` in a **different shape** (`{source:"Internet Movie Database", value:"8.2/10"}`, strings) | `OMDb/MetadataMapper.cs:21-56` |
| The stored `rating` has **no consistent scale** | Measured over the library: Hardcover 2.0-5.0 and Open Library 1.67-5.0 are raw out-of-5 averages; MusicBrainz is converted (x2) to 10; AniList (/10 of a percentage) and IGDB are converted to 10; Steam's is Metacritic/10; TMDB, IMDb, Simkl, TVMaze, TheAudioDB are native /10 |
| Vote counts live in three places | IMDb `extendedData.votes`; Kodi's `CollectRatings` reads top-level `votes` (so Kodi gets null for IMDb); TMDB parses `vote_count` and drops it |
| TheTVDB maps its `score` (a popularity number) to `Rating` | `TheTvdbMetadataProvider.cs:430,469` (plugin not enabled here) |
| A manifest field already reaches the web UI: `sourceUrls` goes manifest -> `PluginsController` -> `PluginSourceUrlDto` -> `listPlugins` | `PluginManifest.cs:93`, `PluginsController.cs:987` |
| Plugins already carry a logo (`iconUrl`, a favicon), name, and which types they support; cards already hide a disabled plugin's data | `ItemPluginPanels.tsx` |
| The red box is empty space: `.stripLibrary` (Status, Rating, Plays) sits in a column with nothing to its right | `MediaDetailPage.tsx` ~1302, `.module.css` `.refreshStrip` |

## 2. Display rule

Each source is shown in **its own scale and style**, never a bare number:

| Style | Looks like | When |
|-------|-----------|------|
| `outOf` (the default) | `8.2/10`, `4.2/5`, `83/100` | whatever the source's scale is |
| `percent` | `80%` | only a source whose site shows a percentage (TMDB) says so |

**A star icon is shown only where the source's own site shows one**, and is declared per source, never inferred from the scale or the style.
IMDb is out of 10 and its site shows a star, so it is `8.2/10` with a star; TMDB shows no star, so it gets none. A source declares `star:
"single"` (one star icon before the number, as IMDb) or `star: "row"` (five stars, the last one filled in proportion, then the number, for a
site that shows a star row). With no declaration there is no star. Before a plugin declares one, its site is looked at.

**Looks like the source.** Each badge uses the source's own mark and colours: its logo image when one can be had (SVG preferred), else its
wordmark set in the source's typeface (or the nearest system font) on its brand colour; never a generic icon. A source whose logo cannot be
sourced legitimately (below) falls back to its wordmark, then to a letter badge.

A plugin declares its source's style and star (3.2). With no declaration the score is `outOf` in the source's native scale and has no
star. Rounding: `outOf` keeps at most one decimal and drops a trailing `.0` (`8/10`); `percent` rounds to a whole number. Next to the
score, small and muted: the vote count when known (`788K votes`; the exact number in the tooltip).

### 2.1 Mimicking the source (widget looks) and sources with more than one score

"Make it look like the score came from the source's site" is data, not code: a manifest declares a `look` for a source, and the host has a
small set of generic renderers that any plugin can pick (no renderer or manifest names a plugin):

| `look` | Renders | Example source |
|--------|---------|----------------|
| `plain` (default) | mark, then the score in the section 2 style | most sources |
| `star-text` | one filled star, the score in bold, `/10` smaller and muted, the vote count under it in grey | IMDb |
| `ring` | a circular progress ring with the whole-number percent inside, ring colour chosen by `thresholds` | TMDB |
| `icon-percent` | a state icon followed by a percent | Rotten Tomatoes (both scores) |
| `badge` | a coloured square holding the number, colour chosen by `thresholds` | Metacritic (green / yellow / red) |

Parameters live in the manifest beside it: `thresholds` (`[{ min, color, icon }]`, so one rule picks both colour and the state icon),
`font_family`, brand colours. Fonts: only open-licence fonts are bundled; where a site's typeface is proprietary the nearest system or
open font is used and a manifest may name a `font_url` the host fetches and caches at runtime like logos (decision 5). Before any source's
`look` is written its real widget is inspected (structure, colours, thresholds); until then it gets `plain`.

**More than one score from one source.** The collector's `variant` (design 5) is the answer, and each variant has its own mark and its own
state icon. Rotten Tomatoes has two: **Tomatometer** (critics; a fresh tomato at 60% or more, a splat when rotten, a certified-fresh seal only
if the source supplies that flag) and **Popcornmeter** (audience; a full bucket at 60% or more, a tipped bucket below). Metacritic likewise has the
**Metascore** (critics) and the **user score**. So a source declares `variants: [{ variant, label, look, thresholds }]`, and the badge
chosen is by `(key, variant)`. TMDB and IMDb are single-variant.

**What data exists.** Showing these needs a plugin that supplies them. OMDb relays only RT's critics Tomatometer and the Metascore, and
shows nothing for the two audience scores. Rotten Tomatoes has no free API, so a dedicated plugin was rejected. **Any plugin that
supplies RT or Metacritic scores in the contract shape (`key: rottentomatoes`, `variant: critics|audience`) is shown with no change to the host**,
whether it is OMDb, a future plugin, or a user-supplied one; and the display is ready for the audience scores the moment one exists.

## 3. Design

### 3.1 One collector, on the server

`ItemScores.Collect(metadataJson, pluginsById)` in `Chronicle.Services` (pure, no database). For every partition of an **enabled**
plugin:

1. **Contract entries first.** Every `extendedData.ratings[]` entry with a finite numeric `value > 0` and `scale > 0` becomes a
   score. Entries of any other shape (OMDb's strings today) are skipped, never guessed at.
2. **Else the plain `rating`.** If it is a finite number `> 0`, it becomes one score for that plugin's own source, converted from
   the scale the plugin *stores* to the scale it *shows*: `native = rating / stored_scale * scale`, both from the plugin's manifest
   (default 10 and 10). This is what keeps Hardcover's raw `4.2` from showing as `4.2/10`.
3. **Votes:** `entry.votes` -> `extendedData.votes` -> partition `votes`.
4. A value above its scale (beyond rounding) is dropped, not clamped.
5. **De-duplicate by `(key, variant)`:** the same measurement relayed by several plugins is shown once, from the plugin that owns
   the key, else from the first plugin in the type's display order; the tooltip names the others.
6. **Order:** the type's saved plugin display order (`plugin_display_order.config`, the order the cards already use); sources with no
   place in it follow alphabetically.

Chronicle's code names no plugin: the logo, label, scale and style all come from manifests.

### 3.2 Manifest: `rating_sources`

The design's optional manifest array, trimmed to what the strip needs; read through the path `sourceUrls` already uses (manifest
-> `PluginsController` DTO -> `listPlugins`) and, on the server, from the loaded manifest:

```json
"rating_sources": [
  { "key": "tmdb", "display_name": "TMDB", "stored_scale": 10, "scale": 100, "display": "percent" }
]
```

Fields: `key` (what is measured), `display_name`, `stored_scale` (the scale of this plugin's plain `rating`; default 10), `scale` and
`display` and `star` (the source's own presentation, above), optional `logo_url` (the source's official logo, an SVG or PNG on the source's own site;
for a relayed source with no plugin of its own, this is its only mark), `wordmark` (`{ text, font_family, color, background }`, the
fallback: the source's name in its typeface and brand colour), `url_template`, `attribution`. `display` is `outOf` (default) or `percent`; `star` is `single`, `row` or absent (no star). A manifest without any of it still works
(3.1 defaults: native scale, `outOf`, no star). No `min_chronicle_version` bump is needed: an
older host ignores the unknown property.

### 3.3 API

`GET /api/v1/media/{id}` gains `ratings: [{ key, variant, displayName, value, scale, display, normalized, votes, url, source,
fetchedAt, alsoFrom }]`, **the field names of the 2026-10-01 design (section 5)**, so the later phases change nothing here. `normalized`
is `value / scale * 100`. Detail only; the library list endpoints are untouched. The field is optional, so an older web client works.

### 3.4 UI

`ScoresPanel` (new component) in the empty space to the right of Status / Rating / Plays:

- The status controls and the panel sit in one wrapping flex row (`.statusAndScores`), so on a narrow window the panel drops below.
- One badge per score. **Mark:** the source's `logo_url` (fetched and cached by the host through the same mechanism that already serves
  `iconUrl` as `/api/v1/plugins/{id}/icon`, so the browser never hot-links a third-party site and a blocked or offline source cannot
  break the page), else its `wordmark`, else the plugin's `iconUrl`, else a letter badge in the brand colour. **Score:** in the
  section 2 style; a star is drawn only for a source that declares one: `single` is one inline-SVG star before the number, `row` is five
  stars with the last partly filled (both `aria-hidden`; the number carries the meaning). Vote count small and muted; the badge links to the source's page when it has a
  `url`; tooltip: reporting plugin, scale, fetched date, other reporters.
- Hidden entirely when there is nothing to show (collections, people, items no plugin scored): no empty box.
- Accessible: each badge has an `aria-label` such as "IMDb score 8.2 out of 10, 787,753 votes". Theme tokens only (light and dark).
- Attribution: where a source's manifest carries an `attribution` sentence (the IMDb dataset licence requires one), it is shown as a
  muted line under the badges.

### 3.5 Plugin updates (small, independent; each its own release)

Required for correct display (without them the result is wrong or bare, section 9):

| Plugin | Change |
|--------|--------|
| IMDb | manifest: `scale 10`, `star: single` (its site shows a star) |
| TMDB | manifest: `stored_scale 10`, `scale 100`, `display percent`, **no star** (its site shows none); keep `vote_count` as `ratings[].votes` |
| Hardcover | manifest: `stored_scale 5`, `scale 5`; also emit `ratings[]` with `ratings_count`; `star: row` only after its site is checked |
| Open Library | same (`ratings_count` is already parsed) |
| MusicBrainz | manifest: `stored_scale 10`, `scale 5`; emit native `ratings[]` (`value/5`, `votes-count`) and stop the x2 in a later release; `star` after its site is checked |
| AniList | manifest: `stored_scale 10`, `scale 100`, `display percent` (star only if its site shows one) |
| IGDB | manifest: `scale 100`; its `ratings[]` already has the native values; style and star after its site is checked |
| Steam | emit `ratings[]` with `key: metacritic`, `scale 100` (today its `rating` is Metacritic/10 labelled as Steam's own) |
| TheTVDB | stop mapping `score` (popularity) to a rating; keep it as `popularity` |
| IMDb, Simkl, TVMaze, Trakt, TheAudioDB | manifest only (native /10, default style) |
| OMDb | emit contract-shaped `ratings[]` (keys `imdb`, `rottentomatoes`, `metacritic`; `icon_url` for the relayed two); keep the original list as `ratings_raw` |

### 3.6 Phases

- **A (this plan):** `ItemScores`, the manifest model field, the DTO field, `ScoresPanel`, tests; the manifest declarations in 3.5.
- **B (separate releases):** the remaining plugin changes in 3.5 (native `ratings[]`, votes).
- **C (later, not built now):** `media_ratings` table, library sort and filter, Kodi `CollectRatings` reading the same collector,
  history, Settings -> Ratings (design sections 3, 5-7).

## 4. Tests

- **Unit, `ItemScores`:** contract entries; plain `rating` with each stored/shown scale (Hardcover 4.2 of 5, MusicBrainz 8.4 stored -> 4.2/5,
  AniList 8.2 -> 82%, TMDB 7.984 -> 80%); skipped shapes (OMDb strings, missing scale, zero, negative, NaN, value above scale);
  votes in each of the three places; de-duplication and the "alsoFrom" list; ordering; a disabled plugin's score hidden; a removed
  plugin's absent.
- **Golden test from real data:** the recorded 1917 partitions -> `IMDb 8.2/10 (787,753 votes)`, `TMDB 80%`, `Simkl 6.5/10`.
- **Formatting (frontend):** every style and the rounding rules; **no star unless declared** (an IMDb-like source gets one single star, a TMDB-like
  source none, a five-point source with no declaration none); the star row's fill widths (4.2 -> four full stars and a fifth filled 20%; 5.0 -> five
  full; 0.4 -> 40% of the first); the mark fallback chain (logo -> wordmark -> icon -> letter badge); the vote-count abbreviations; the hidden state.
- **Integration:** `GET /api/v1/media/{id}` includes `ratings` and an older shape still deserialises.
- **Plugin tests:** each updated plugin's manifest declarations are valid, and each emitted `ratings[]` entry is in contract shape.
- **Rule check:** a test that no host source file under `Chronicle.Services/Scores` names a plugin.

## 5. Out of scope

The `media_ratings` table; library sort/filter by score; Kodi changes; rating history; a Settings -> Ratings page; the user's own
rating (unchanged); Building a Rotten Tomatoes *plugin* (no free API; rejected). Its scores are shown whenever any plugin supplies them (2.1).

## 6. Risks

| Risk | Mitigation |
|------|-----------|
| A wrong scale shown as if right (the owner's "4 is not fine") | per-plugin `stored_scale` and `scale`; golden and per-scale tests; never guess from the number alone |
| A relayed or derived score shown under the wrong name (Steam's Metacritic; IMDb's episode average) | `key` names what is measured; variants shown; Steam changed in 3.5 |
| Logos are third-party favicons (small, hot-linked, can fail) | the existing `iconUrl` mechanism; letter-badge fallback |
| Attribution and trademark terms (IMDb dataset sentence; TMDB's required notice) | show the manifest `attribution` line; keep the TMDB notice wherever its logo is used |
| Older manifests and plugins | every field optional; defaults follow the legacy contract; unknown JSON properties are ignored |

## 7. Decisions needed from the owner

1. **TMDB style:** `80%` (what TMDB's site shows; planned) or `8/10`.
2. **Show every reporter or one per measurement?** Planned: one per measurement (the owner of the key wins), others in the tooltip.
3. **Votes:** shown small next to the score (planned) or only in the tooltip.
4. **Phase B plugin releases:** one per plugin as each is ready (planned), or all together.
5. **Logos:** may Chronicle fetch each source's official logo file at runtime and cache it (planned: nothing is bundled in the repo, and each
   source's own usage terms are followed), or do you want the logos downloaded once and committed? Downloading is something I will not do
   without your say-so (I would state each file name, source and size first). Where no logo can be had, the wordmark in the source's
   typeface and colour is used.
6. **Which other sources show a star?** You gave IMDb (star) and TMDB (none). For every other plugin I will look at its site before
   declaring anything, and a source I could not verify gets no star (the safe default). Tell me if you already know any.

7a. **Look fidelity (2.1):** the widget kinds above are built generically; each source's exact look needs its site inspected first, which the built-in browser could not open (TMDB was refused). May I use Claude in Chrome, or will you allow the site, to inspect them?

## 8. Order of work

1. `PluginManifest.RatingSources` + DTO + `listPlugins`; 2. `ItemScores` + unit and golden tests; 3. `ratings` on the detail DTO + integration
test; 4. `ScoresPanel` + frontend tests + layout; 5. manifest declarations for the plugins in 3.5; 6. verify on real items (1917, a book, an
album, a game, a show); 7. docs (PLUGIN_DEVELOPMENT_GUIDE, CLAUDE.md) and release; 8. phase B plugin releases.

## 9. Review (before development)

Reviewed against the code and the library data, trying to break the plan. Findings and what each changed:

| # | Severity | Finding | Change to the plan |
|---|----------|---------|--------------------|
| R1 | High | The stored `rating` has no consistent scale (measured: Hardcover and Open Library raw /5, MusicBrainz/AniList/IGDB converted to /10, TMDB /10 shown by its site as %). A single assumption would show Hardcover `4.2/10`, exactly the bare-number case the owner ruled out | Added `stored_scale` and `scale` per plugin (3.1 step 2, 3.2) and made the Hardcover/Open Library/MusicBrainz manifest declarations **required** in phase A, not optional |
| R2 | High | Steam's `rating` is Metacritic divided by 10, labelled as Steam's score; TheTVDB's `rating` is a popularity number | Steam emits `key: metacritic`; TheTVDB stops emitting a rating (3.5); the collector's `key` names what is measured |
| R3 | Medium | OMDb's `ratings` list has a different shape (strings); a naive parser would throw or show garbage | Collector accepts only numeric `value` and `scale` and skips anything else; test added; OMDb change moved to 3.5 |
| R4 | Medium | Votes are in three places and Kodi's reader misses IMDb's | Collector reads all three (3.1 step 3); making Kodi use the collector stays phase C |
| R5 | Medium | The same score is relayed by several plugins (IMDb via IMDb, OMDb, Simkl); showing all would repeat it and imply they differ | De-duplicate by `(key, variant)`, owner first, others in the tooltip (3.1 step 5) |
| R6 | Medium | A disabled plugin keeps its data, so its score would still show while its cards are hidden | Collector takes enabled plugins only; test added |
| R7 | Medium | No rule for rounding, so TMDB `7.984` could print `7.984` or `79.84%` | Section 2 fixes the rounding per style; golden test pins `80%` |
| R8 | Medium | Items with no score (collections, people, most episodes) must not leave an empty bordered box | Panel hidden entirely when empty (3.4) |
| R9 | Medium | The API shape could diverge from the 2026-10-01 design and force rework in phase C | Field names taken from design section 5 (3.3) |
| R10 | Low | Logos are hot-linked favicons: small, can be blocked or offline | Reuse the existing `iconUrl` mechanism; letter-badge fallback (3.4) |
| R11 | Low | Attribution and trademark terms for IMDb and TMDB | Manifest `attribution` shown under the badges (3.4); noted in 6 |
| R12 | Low | A manifest-only change must not break older hosts | Unknown JSON properties are ignored; no `min_chronicle_version` bump (3.2) |
| R13 | Low | The rule "Chronicle knows no provider by name" could be broken by a convenient `if (source == "imdb")` | A test over the new host code (section 4); all presentation comes from manifests |
| R14 | Low | The layout beside the status controls could overflow on narrow screens or with many sources | One wrapping row; the panel wraps below (3.4) |
| R15 | High | "Same font or image as the source" cannot be met by hot-linking favicons (16-32 px, blurry, blocked offline) or by bundling proprietary typefaces (licence) | Logos come from the source's own `logo_url`, fetched and cached by the host through the existing icon path; the typeface is reproduced only as a *wordmark fallback* with the source's `font_family` as a system-font stack, never a bundled font file (2, 3.2, 3.4) |
| R16 | Medium | Official logos have usage terms (TMDB requires its attribution notice and prescribes logo use; IMDb restricts its mark) | Fetched at runtime and cached, not committed; the manifest `attribution` is shown; each source's terms are checked at build time and a source whose terms forbid the use falls back to its wordmark (decision 5) |
| R17 | Medium | An earlier draft derived the star style from a five-point scale. Wrong: IMDb is out of 10 and shows a star; TMDB shows none; a five-point source need not show stars | A star is **declared per source** (`star: single` / `row`), never inferred; no declaration means no star; sources are checked against their sites before declaring (2, 3.2, decision 6). A star row's fractional fill is unit-tested (4.2 -> four full stars and a fifth 20% filled) |
| R18 | Low | Logo images need light and dark variants and a fixed box, or the row jumps as they load | Fixed-height badge box (no layout shift), an optional dark-theme `logo_url`, and the wordmark shown until the image loads |

Checked and found sound: the data needed is already in the item (no new fetch, no migration); computing on the detail request only has
no performance cost (the partitions are already loaded for `pluginMetadata`); the manifest-to-UI path exists (`sourceUrls`); the
design's later phases are unaffected.

**Verdict:** approved to build once the decisions in section 7 are answered (defaults are marked "planned" if the owner has no preference). Decision 5 (how logos
are sourced) is the only one that blocks a *part* of the work: everything else can start, with the wordmark fallback in place, while it is open.
