# Ratings & Scores Tracking — Design

**Date:** 2026-10-01
**Status:** Design, not started
**Goal:** Chronicle keeps every public score or rating any provider gives it (with vote counts),
shows them, lets the library sort and filter by them, and sends them all to Kodi. All of this
stays generic: a new provider's scores appear without core or UI code changes. Like every
other field, which provider's value wins is decided by the user's precedence settings
(Metadata Assignment).

Related: `2026-10-01-plugin-declared-fields-design.md`. Each score key is a registered field there.

---

## 1. Where things stand today

| Area | Today | Problem |
|------|-------|---------|
| Plugin contract | `MediaMetadata.Rating` — one `double?`, assumed 0–10 | One number per provider; no vote count; no way to report a second score (critics vs audience, or a provider relaying another site's score) |
| Storage | The number sits in the provider's `metadata_json` partition; `_resolved.rating` holds the winner of Metadata Assignment priority | Not queryable: sorting or filtering by "IMDb ≥ 7.5" means parsing JSON for every item |
| Vote counts | Dropped. TMDB parses `vote_count` and never passes it on; Simkl parses `votes` and drops it | A 9.8 from 12 votes looks the same as a 9.8 from 2 million |
| Other scores providers already send | Simkl's `ratings` object has more than its own score, but only `simkl` is parsed | Free data thrown away |
| UI | One "★ public rating" (the resolved one) on cards and the detail page; library sorts only by the user's own rating | Can't see or compare scores across sources |
| Kodi | `ScraperController.CollectRatings` sends one rating per provider partition | Fine for one-score providers; can't send more |
| TheTVDB | `Rating = s.Score` | In TVDB's v4 API `score` is a popularity number, not a 0–10 rating. Latent (the plugin isn't enabled on the live server), but it would rank shows by popularity as if it were quality |

---

## 2. Plugin contract

Add to `MediaMetadata`:

```csharp
/// <summary>Every public score this provider knows for the item -- its own and any it relays
/// from another site. Empty when the provider has none. The legacy <see cref="Rating"/> is
/// still honoured: when Ratings is empty, Chronicle treats Rating as one entry keyed by the
/// provider's own source.</summary>
public List<ProviderRating> Ratings { get; set; } = [];

public record ProviderRating(
    string  Key,           // what is measured: "imdb", "tmdb", "trakt", "metacritic" ...
    double? Value,         // in the source's own scale; null = "no score yet", never 0
    double  Scale,         // 10, 5, 100 ...
    int?    Votes = null,  // count the score is based on
    string? Variant = null,  // when a source has several scores: "critics", "audience", "top-critics"
    string? Url = null);   // link to the score's page on the source, if the plugin has one
```

Rules:

- **Native scale, not pre-converted.** MusicBrainz sends 4.2 / 5, not 8.4. Core does the
  normalization once (`Value / Scale * 100`), so no plugin gets it wrong.
- **`Key` names what is measured, not who reported it.** Simkl relaying IMDb's score sends
  `Key = "imdb"`. Core records the reporting plugin separately (§3), so provenance is kept.
- `Rating` stays for backwards compatibility. Every existing plugin keeps working unchanged.
  Updated plugins set `Ratings` and stop setting `Rating`.

Because enrichment serializes the whole `MediaMetadata` into the provider's partition, `ratings`
lands in `metadata_json` automatically. This keeps the lossless-ingestion rule with no extra
code.

---

## 3. Storage: `media_ratings`

A derived, queryable index of every score. It is not a new source of truth: it is rebuilt from
the partitions, exactly like `_resolved`.

| Column | Type | Notes |
|--------|------|-------|
| `Id` | int PK | |
| `MediaItemId` | int FK → media_items, cascade delete | |
| `RatingKey` | text | `imdb`, `tmdb`, … |
| `Variant` | text, default `''` | `''` when the source has one score |
| `SourcePluginId` | text | full plugin id that reported it (matches partition keys) |
| `Value` | real | native scale |
| `Scale` | real | |
| `Normalized` | real | 0–100; what sorting and filtering use |
| `Votes` | int null | |
| `Url` | text null | |
| `FetchedAt` | datetime | when the provider last returned it |
| `IsResolved` | bool | true on the row that won precedence for this key/variant (§3b) |

Unique index on `(MediaItemId, RatingKey, Variant, SourcePluginId)`. Query indexes on
`(RatingKey, Variant, Normalized)` and `(RatingKey, Variant, Votes)`.

**Written in one place:** `MetadataResolutionService.ResolveAsync`. Every path that changes
metadata (enrichment, refresh, merge, sync, file scan, bulk recompute) already funnels through it.
It replaces the item's rows from the current partitions, so a provider that stops returning a
score also stops showing one.

### 3b. Precedence

Every row from every plugin is kept: nothing is discarded because another plugin also reported
that score. Which one Chronicle *uses* is the user's call, through the normal precedence settings:

- **Each score is a field.** Every `(RatingKey, Variant)` is registered in the field registry
  as `rating.{key}` / `rating.{key}.{variant}` (e.g. `rating.imdb`, `rating.tmdb`,
  `rating.imdb.episodes-avg`). It appears in Metadata Assignment per media type and level,
  listing every plugin that supplies it, in the user's chosen order. Example: the IMDb plugin's
  `rating.imdb` ahead of Simkl's relayed copy, or the reverse.
- `ResolveAsync` marks the winning row `IsResolved`; the UI, sorting, filtering and Kodi read
  resolved rows only. Pins (`_overrides`) work on scores like on any other field.
- **The headline ★ rating** stays the existing `rating` field, also chosen by precedence: the
  user orders which plugin's own score is the default. Nothing new to configure; IMDb's score
  becomes the default only if the user puts IMDb first.
- **Default ordering before the user sets one:** the plugin whose own source is that key
  (e.g. the IMDb plugin for `rating.imdb`), then others. It's only a starting point; it never
  overrides a saved order.

**Housekeeping:** `MergeService` re-points the loser's rows, keeping the winner's on conflict.
The library reset and the delete paths clear them. A one-time backfill job builds rows from
every existing partition's `rating`, so scores show up immediately without any re-fetch. Vote
counts fill in as items get refreshed.

### 3a. History (phase 2)

`media_rating_history (MediaItemId, RatingKey, Variant, SourcePluginId, Value, Votes, RecordedAt)`,
with a row inserted only when Value or Votes actually changes. Off by default (App Setting
`ratings.keep_history`). It's cheap, since most catalogue scores barely move. This is what
makes "IMDb rating over time" or "biggest movers in my library" possible later.

---

## 4. Describing rating sources: manifest, not code

New optional `rating_sources` array in a plugin's `manifest.json`:

```json
"rating_sources": [
  {
    "key": "imdb",
    "display_name": "IMDb",
    "short_name": "IMDb",
    "scale": 10,
    "kodi_name": "imdb",
    "url_template": "https://www.imdb.com/title/{id}/",
    "attribution": "Information courtesy of IMDb (https://www.imdb.com). Used with permission."
  }
]
```

Core merges these into a `rating_sources` registry (DB-backed, like media types), keyed by
`key`. The UI reads it for labels, brand colours (already in manifests as
`brandColorLight/Dark`), links and attribution text. A key no manifest describes still works
and shows its raw key, so nothing is ever hidden for lack of metadata.

`attribution` is shown wherever that source's score is displayed. IMDb's dataset licence
requires that exact sentence; other sources can use the same field.

---

## 5. API

- `GET /api/v1/media/{id}` gains `ratings: [{ key, variant, displayName, value, scale,
  normalized, votes, url, source, fetchedAt }]` (resolved rows), plus `allRatings` with every
  plugin's row so the detail page can show what each provider reported.
- Library/list endpoints gain a compact `ratings` map (`{ "imdb": 87, "tmdb": 82 }`, normalized)
  so cards can render and sort client-side without a second call.
- `GET /api/v1/ratings/sources` returns the registry plus, per key, how many library items have
  a score, which the UI uses to offer only sort/filter options that have data.
- Library query parameters: `ratingKey`, `minRating` (normalized), `minVotes`,
  `sort=rating:{key}` so a large library can sort and filter server-side through the indexes.

---

## 6. UI

- **Detail page, Ratings strip:** one badge per score. Each shows the source's name in its
  brand colour, value / scale, a vote count ("2.3M votes"), a link to the source, and a tooltip
  with the reporting plugin and fetch date. The user's own rating sits at the start of the strip,
  labelled as theirs. Attribution lines render under the strip for the sources shown.
- **Library and collection cards:** the "★" number is the resolved `rating` field, so which
  source it shows is set in Metadata Assignment like every other field.
- **Library sort/filter:** "IMDb rating", "TMDB rating", … built from
  `/ratings/sources`, plus a minimum-votes filter so obscure 10/10s can be excluded.
- **Settings → Ratings:** display-only choices (badge order in the strip, hidden badges) and the
  history toggle. Which provider's value is used is not set here; that's Metadata Assignment.

---

## 7. Kodi

`ScraperController.CollectRatings` reads `media_ratings` instead of partitions and emits one
entry per key/variant, named by the source's `kodi_name` (`imdb`, `themoviedb`, `trakt`, …;
the key itself when no `kodi_name` is given). The resolved/default rating goes first in the
dict. The addon's `apply_ratings()` already marks the first entry as Kodi's default and loops
over any number of entries, so **no addon change is needed**. Votes are already in
`ScraperRatingDto`; they finally get real values.

---

## 8. Provider updates (each small, independent)

| Plugin | Change |
|--------|--------|
| TMDB | Emit `tmdb` with `vote_count` (movies, shows, seasons, episodes) |
| Trakt | Emit `trakt` with `votes` |
| Simkl | Parse the full `ratings` object: `simkl`, plus any other sources it relays (e.g. `imdb`, `mal`), each with votes. *Verify the live response shape first.* |
| TVMaze | Emit `tvmaze` (average only; it has no vote count) |
| Hardcover | Emit `hardcover` /5 with `ratings_count` |
| MusicBrainz | Emit `musicbrainz` /5 with `votes-count` (stop ×2 in the plugin; core normalizes) |
| FanEdit / MRDb | Emit their site scores with whatever count the page gives |
| TheTVDB | **Stop mapping `score` to a rating** (keep it in ExtendedData as `popularity`) |
| IMDb (new) | `imdb` with `numVotes` for every title, season roll-up, episode. See `PLUGIN_IMDB.md` |

## 9. Build order

1. Contract (`Ratings`, `ProviderRating`) + table + `ResolveAsync` writer + backfill job.
2. API fields + detail-page strip + manifest `rating_sources` registry.
3. Kodi `CollectRatings` switch.
4. Provider updates (TMDB first, since it covers the most items).
5. Library sort/filter + Settings → Ratings.
6. History table (optional).
