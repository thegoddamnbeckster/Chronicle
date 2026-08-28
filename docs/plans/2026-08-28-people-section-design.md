# People Section — Design

Adds a first-class "People" section to Chronicle: every actor/director/author/narrator/etc.
gets a poster-grid entry, a detail page showing what they're credited on grouped by role, and a
full Wikipedia biography. Builds directly on `PLUGIN_WIKIPEDIA_V4.md`, which already scoped a
`people` `MediaTypeSupport` entry with `DisplayName` deliberately left empty pending this design
— this document is what flips that switch.

Per your direction, this covers the fuller version: real person-ID-linked credits sourced from
TMDB, MusicBrainz, and Hardcover's own APIs (not a name-matching workaround), across movies/TV,
music, and books/audiobooks in one pass.

**Scope reality check up front:** this touches five repos (`Chronicle`, `Chronicle.Plugin.TMDB`,
`Chronicle.Plugin.MusicBrainz`, `Chronicle.Plugin.Hardcover`, `Chronicle_Scraper`) and a genuinely
new relational concept (people as first-class, credit-linked entities) that nothing in the
current schema does today. The Phasing Plan (Section 9) sequences this into dependency-ordered,
independently-shippable chunks rather than one large change.

---

## 1. Data Model

### 1.1 People are `MediaItem`s, `MediaTypeName = "people"`

Reuses the entire existing generic infrastructure for free: `poster_url`, `overview`,
`metadata_json` (Wikipedia's full bio/sections/images per `PLUGIN_WIKIPEDIA_V4.md` lands here
unchanged), `media_external_ids`, global search (already confirmed to search all `media_items`
regardless of type with zero filtering), the plugin-priority resolution/override system, and
`PluginMetadataBox`'s automatic per-plugin rendering on the detail page — this is what makes
"detail page includes the full information received from Wikipedia" essentially free once
enrichment is wired up: no new rendering code needed, the existing generic box already does it.

**`PLUGIN_WIKIPEDIA_V4.md` Section 12 update:** the `people` row's `DisplayName` flips from
empty to `"People"`, `HierarchyLevels = 1`, `HierarchyLabels = null` (flat, no sub-levels),
`InteractionVerb`/`ProgressUnit` are set to inert defaults (`"viewed"` / `"percent"`) since
people are **not** tracked through the watch/library/interaction system at all (Section 1.4) —
these two fields exist only because `MediaTypeSupport` requires *some* value, not because
anything reads them for this type. `SupportsCollections = false`. Wikipedia becomes the
canonical registrant of the `people` type via `PluginHostService.SyncMediaTypesFromPluginsAsync`
— no manual seeding, same mechanism that already registers every plugin-driven type today.

### 1.2 `media_credits` gains a real person link

Current schema (confirmed): `media_credits(id, media_item_id, person_name, role, character_name,
billing_order, source, external_person_id, created_at)` — flat, no FK to a person, written only
by Trakt's import-sync path.

**Migration adds:**
```sql
ALTER TABLE media_credits ADD COLUMN person_media_item_id INTEGER NULL
  REFERENCES media_items(id) ON DELETE SET NULL;
CREATE INDEX idx_media_credits_person_item ON media_credits(person_media_item_id);
```
Nullable, and stays nullable long-term — a credit whose person can't be confidently resolved
(Section 3) still gets stored (provenance preserved via `person_name`/`external_person_id`) but
simply won't appear on any person's detail page until/unless resolved. `person_name` and
`external_person_id` are kept as-is, not replaced — they remain the provenance record and the
resolution key; `person_media_item_id` is a derived pointer, not the source of truth.

**Backfill:** a one-time startup migration step resolves existing Trakt-sourced rows
(`source = 'trakt'`) against the new person-resolution logic (Section 3) — best-effort, using
`person_name` + `external_person_id` (Trakt's own numeric person ID, already captured today per
the earlier research: `ExternalPersonId = actor.Person.Ids?.Trakt?.ToString()`).

### 1.3 Two new canonical fields: `birth_date`, `death_date`

One-line additions to `MetadataResolutionService.FieldMap` (`["birth_date"] = ["birthDate"],
["death_date"] = ["deathDate"]`), populated from Wikipedia's `ExtendedData.bornDate`/`diedDate`
(already designed in `PLUGIN_WIKIPEDIA_V4.md` Section 7). Unlike most canonical fields, these
are **promoted to first-class nullable columns** on `MediaItem` (`BirthDate DateTime?`,
`DeathDate DateTime?`), matching the existing precedent for `poster_url`/`year`/
`runtime_minutes` — needed because the People grid (Section 5) has to render the deceased badge
and birth/death text on every card cheaply, without requiring per-card resolution-blob parsing.
Promotion follows the same explicit-`if`-block pattern `MetadataResolutionService.ResolveAsync`
already uses for the other promoted fields (lines 124-139 per the earlier research) — two more
branches, not new machinery. The pin/override system (`_overrides`) works for these fields for
free the moment they're added to `FieldMap`, per the same research finding.

### 1.4 People are catalog-wide, not per-user library items

"All people that are in Chronicle should have a poster style entry" — read literally, and
consistent with no watch-status concept making sense for a person: the People page (Section 5)
lists **every** `people`-type `MediaItem` in the catalog, unfiltered by any single user's
library. No `user_libraries` rows are created for people, no watch/completion status, no
interaction events. A person exists in Chronicle purely because they're credited on something
(Section 3) or were directly searched/added. This is a deliberate scope decision, not an
oversight — if per-user "follow this person" tracking is ever wanted, it's a separate, additive
feature layered on top of this catalog, not a redesign of it.

### 1.5 Headshot accumulation — new table, not a blob field

Confirmed (research): the existing artwork system is **entirely fetch-and-replace** — every
plugin's `AdditionalImages` list is overwritten wholesale on each enrichment run, with only the
`_overrides` pin surviving replacement by design. "Store every headshot ever found, most recent
as default, user can override" needs a genuinely new accumulating store — there's no precedent
to extend.

```sql
CREATE TABLE person_headshots (
    id                    INTEGER PRIMARY KEY,
    person_media_item_id  INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
    url                   TEXT NOT NULL,
    thumbnail_url         TEXT NULL,
    source                TEXT NOT NULL,   -- plugin id, e.g. "chronicle.plugin.tmdb"
    first_seen_at         TEXT NOT NULL,   -- ISO-8601 UTC
    UNIQUE(person_media_item_id, url)
);
CREATE INDEX idx_person_headshots_person ON person_headshots(person_media_item_id);
```

Rows are **inserted, never overwritten or pruned** (`INSERT OR IGNORE` on the unique
`(person_media_item_id, url)` pair — a headshot seen again on re-enrichment is a no-op, not a
timestamp bump; "most recent" means most-recently-*discovered*-by-Chronicle, not the photo's
real-world capture date, which no provider reliably exposes anyway).

**Two feed paths, both writing into the same table:**
1. **A person's own enrichment** — when Wikipedia (or any future `people`-type provider) returns
   `MediaMetadata.PosterUrl` for the person directly, it's inserted here (in addition to, not
   instead of, the normal per-plugin blob write — the existing per-plugin metadata storage is
   untouched, this is additive).
2. **Credit resolution on someone else's title** — when a movie/show/album/book is enriched and
   its `Cast`/`Crew` entries carry a `ProfileImageUrl` (Section 2 — TMDB's `profile_path`, the
   only current source of this on the credit path), `PersonResolutionService` (Section 3) inserts
   it against the resolved person, tagged with the *title's* enriching plugin as `source` even
   though the image itself depicts the person, not the title — provenance is about who supplied
   the URL, not the image's subject.

**Resolution:** for `people`-type items, `PosterUrl` resolution is special-cased in
`MetadataResolutionService.ResolveAsync` — instead of the normal per-plugin-blob priority walk,
it checks `_overrides.poster_url` first (unchanged — an explicit user pin always wins, exactly
"just like how posters and things work in the media detail," reusing that mechanism verbatim),
then falls back to `SELECT url FROM person_headshots WHERE person_media_item_id = ? ORDER BY
first_seen_at DESC LIMIT 1`. This is the one field on this one type where resolution source-shape
differs from every other field/type — worth calling out explicitly since it's an exception to the
otherwise-uniform per-plugin-blob model, made necessary by headshots being genuinely
cross-plugin-source and accumulating rather than siloed per plugin.

**Picker UI:** no new component. `imageExtractor.ts`'s `extractSlottedImages()` (client-side
aggregation, confirmed as the existing mechanism `AdditionalImagesCard`/the detail-page lightbox
already use) gets one additive data source: a new `personHeadshots: HeadshotDto[]` array on
`MediaItemDto` for `people`-type items, merged into the same `SlottedImageEntry[]` the poster
slot's gallery already renders. The existing `SetOverride`/`ClearOverride` mutations, the existing
lightbox, the existing "pin any image to any slot" UI — all work unmodified. This is the one part
of the whole feature that's genuinely free, by design, because it was scoped to reuse the exact
mechanism you pointed at ("just like how posters and things work").

---

## 2. Plugin Extensions — Core Model Change

`Chronicle.Plugins/Models/MediaMetadata.cs` — `CastMember`/`CrewMember` gain two optional
trailing fields (non-breaking — existing positional-record construction in every plugin still
compiles unchanged):

```csharp
public record CastMember(string Name, string? Role = null,
    string? ExternalPersonId = null, string? ProfileImageUrl = null);
public record CrewMember(string Name, string? Job = null,
    string? ExternalPersonId = null, string? ProfileImageUrl = null);
```

`ExternalPersonId` convention: `"{source}:{id}"`, e.g. `"tmdb:287"`, `"musicbrainz:{mbid}"`,
`"hardcover:{id}"` — matches the cross-ref ID convention already established elsewhere in
`PLUGIN_AUTHORING.md`. This is what `PersonResolutionService` (Section 3) keys dedup on.

**New write path — nothing today moves `Cast`/`Crew` into `media_credits`.** Confirmed: the only
existing writer is `SyncOrchestrationService.FetchAndStoreCreditsAsync` (Trakt's
`IImportProvider.GetCreditsAsync` path). A new hook in `MetadataEnrichmentService`, run
immediately after a `people`-eligible-parent item's metadata merge completes, walks the merged
result's `Cast`/`Crew` and calls `PersonResolutionService.ResolveAndRecordCreditAsync(...)` once
per entry. This is additive to the existing merge flow, not a replacement of it — the flat
`cast`/`crew` JSON keys in `metadata_json` (already rendered generically today) keep working
exactly as they do now; `media_credits` becomes a second, queryable, person-linked
representation of the same information.

---

## 3. `PersonResolutionService` (new)

Given `(PersonName, ExternalPersonId?, Source, ProfileImageUrl?, Role/Job, CharacterName?,
BillingOrder?, TitleMediaItemId)`:

1. If `ExternalPersonId` present: look up `media_external_ids` for
   `(Source = shortSourceName, ExternalId = theId)` scoped to `people`-type items. Hit → that's
   the person.
2. Else: look up an existing `people`-type item by exact `NormalizedName` match (same
   normalization function movies/shows already use for their own dedup).
3. Else: create a new stub `people` `MediaItem` (`IsStub = true`, `HierarchyLevel = 0`,
   `Name = PersonName`, `NormalizedName = normalize(PersonName)`).
4. If `ExternalPersonId` present and not yet recorded for this person, write a
   `media_external_ids` row — **reuses the existing table as-is**, no new schema needed for this
   part.
5. If `ProfileImageUrl` present, insert into `person_headshots` (Section 1.5, feed path 2).
6. Write the `media_credits` row: same delete-and-reinsert-per-`(item, source)` pattern
   `FetchAndStoreCreditsAsync` already uses (kept for consistency, not reinvented as an upsert —
   simpler and lower-risk than introducing new dedup-key semantics), now also setting
   `person_media_item_id`.
7. If step 3 created a brand-new stub, seed it into the enrichment queue the same way any new
   stub item gets seeded today (Add Media's existing "new item → pending enrichment rows for
   every type-compatible plugin" path) — this is what gets Wikipedia's bio/photo/sections onto a
   person who was first discovered as a bare name in someone else's cast list, with zero new
   enrichment-triggering code: it's the same mechanism, just invoked from a new call site.

**Known limitation, stated plainly:** name-only resolution (steps 2-3, when no
`ExternalPersonId` is available — e.g. MusicBrainz band-member relations before the MBID-threading
extension below ships, or any plugin credit that genuinely has no ID) carries the same
common-name collision risk already documented in `PLUGIN_WIKIPEDIA_V4.md` Section 13 for
Wikipedia's own people search. Two different real "John Smith"s credited on different titles with
no external ID on either credit will merge into one person item. This is a real, accepted
limitation of name-based fallback matching, not something this design eliminates — it's mitigated
(not solved) by preferring ID-based resolution wherever a plugin supplies one, which is why the
per-plugin extensions below prioritize threading IDs that already exist in each API's response
over inventing new heuristics.

---

## 4. Per-Plugin Extensions

### 4.1 TMDB — low risk, no new API calls

Confirmed: TMDB's credits response (fetched today via `append_to_response=credits` on the main
movie/show detail call — no separate request) already includes `id`, `profile_path`,
`known_for_department`, `gender` per cast/crew member; the plugin's DTOs
(`TmdbCastMember`/`TmdbCrewMember`) simply don't declare fields for them, so
`System.Text.Json` silently drops them on deserialize.

**Change:** add `Id`/`ProfilePath` to both DTOs in `TmdbModels.cs`, thread through at the two
existing mapping sites (`TmdbMetadataProvider.cs:722-723` movie, `:790-791` tv):
```csharp
new CastMember(c.Name, c.Character,
    ExternalPersonId: $"tmdb:{c.Id}",
    ProfileImageUrl: c.ProfilePath is null ? null : $"https://image.tmdb.org/t/p/h632{c.ProfilePath}")
```
(`h632` — TMDB's largest standard profile-image size; matches the existing `BuildImageUrl`
size-code pattern this plugin already uses elsewhere.) No new endpoint, no new rate-limit
exposure — this is a pure mapping fix over data already being fetched. Lowest-risk of the three
plugin changes.

### 4.2 MusicBrainz — mixed: some free, some genuinely new fetching

Confirmed: MBIDs for band members (`artist-rels` on the artist entity) and composer/lyricist/
arranger (`work-rels` via the linked work) are **already parsed into local variables and then
discarded** before reaching `CastMember`/`CrewMember` — identical shape to the TMDB fix, just at
different call sites (`MusicBrainzEntityFetcher.cs` — band members around line 66-71, work
credits around line 258-266/301-304). Threading these through is the same low-risk change as 4.1.

**What's genuinely new work:** recording-level performer/producer/engineer relations are already
*fetched* (`RecordingIncludes` already requests `artist-rels`) but never filtered/mapped at all
today (only the recording's link to its `Work` is currently used, to chase composer credits) —
this needs new mapping logic, not just ID-threading. Album/release-level production credits
(producer, engineer of the release itself, as opposed to the recording) need new `inc=`
parameters added to `ReleaseGroupIncludes`/`ReleaseIncludes` (`artist-rels`/`recording-rels`) —
genuinely new API surface for this plugin, with the attendant rate-limit consideration already
designed into `MusicBrainzClient`'s throttle (no change needed there, just more calls to
throttle).

**Recommendation:** ship the free part (band members, composer/lyricist/arranger) in the same
pass as the TMDB change — it's the same class of fix. Treat recording/release-level production
credits as a follow-on within this same plugin, since it requires actually designing the
relation-type-to-role mapping (MusicBrainz's relation type vocabulary is large and inconsistently
used across releases) rather than just wiring up already-parsed data.

### 4.3 Hardcover — author ID is free; narrator support has an open unknown

Confirmed: `contributions[].author.id` is already fetched and parsed into `HcAuthorStub.Id`, then
discarded in `BuildCast` (`HardcoverMetadataProvider.cs:1180-1190`). Threading it through as
`ExternalPersonId = $"hardcover:{c.Author.Id}"` is the same low-risk fix as 4.1/4.2's free part.

**Narrator support is not currently implementable as designed** — confirmed the GraphQL query no
longer requests `narrations` (the field was removed upstream; the query has a literal `#
narrations removed — field no longer exists in Hardcover API` comment), and `HcNarratorStub` has
no `Id` field even in the dead code that still references it. Since "narrator" was explicitly one
of your example roles, this needs a real answer before implementation, not an assumption: **check
Hardcover's current live GraphQL schema** (introspection query or their published docs) for
whether narrator credit now lives inside `contributions[].contribution` as a role value (e.g.
`contribution: "Narrator"` alongside the same `author { id name }` shape already fetched for
authors) or has moved somewhere else entirely. This is flagged as an open implementation item
(Section 10), not resolved here — it's a live-API question this design can't settle by reading
existing code.

---

## 5. Frontend — People List Page

**Nav** (`Layout.tsx`): a new `<NavLink to="/people">People</NavLink>` alongside the existing
`<NavLink to="/library">Library</NavLink>` inside the same `NavGroup label="Library"` — "same
level as Library" read literally, as a sibling within that group rather than a new top-level
group.

**Route** (`App.tsx`): `<Route path="people" element={<PeopleLibraryPage />} />` and
`<Route path="people/:id" element={<ErrorBoundary context="Person Detail"><PersonDetailPage />
</ErrorBoundary>} />`, registered inside the same authenticated `Layout` wrapper as every other
page.

**`PeopleLibraryPage`** — a new component, not a generalization of `LibraryPage` (confirmed
`LibraryPage` is tightly bound to watch-status/`LibraryEntry` semantics that don't apply here).
Mirrors its *shape*, not its data model:
- Data source: a new `GET /api/v1/people` (or `GET /api/v1/media?mediaTypeName=people` if the
  existing generic media-list endpoint already supports type filtering — reuse it if so) —
  catalog-wide, not library-scoped, per Section 1.4.
- Sort: `Name`, `Birth Date`, `Recently Added` (`CreatedAt`) — drops `rating`/`status`
  (no watch-status), keeps the general shape of the existing sort `<select>`.
- Filter: by **position/role** — a multi-select built from the distinct `Role`/`Job` values
  present across that person's `media_credits` rows (server returns the distinct-roles list
  alongside the page, or a dedicated small endpoint); by **deceased** (yes/no/either) as a
  secondary toggle, straightforward given `death_date` is now a first-class column (Section 1.3).
- Grouping: none by media-type sub-section the way Library groups by type — People is already
  one type, so it's a flat grid.

**`PersonCard`** — a new small component (not a `LibraryPage`-grid-cell fork), built the same way
that grid cell is built: `PosterImage` (fully generic, headshot-ready as-is per research) inside a
`position: relative` wrapper, plus overlay badges using the exact same absolutely-positioned-`div`
pattern already used for the file/collection/stub badges. Concretely:

```tsx
<div className={styles.personCard}>
  <div className={styles.poster}>
    <PosterImage posterUrl={person.posterUrl} name={person.name} lazy />
    {person.deathDate && <div className={styles.deceasedBar} title={`Died ${formatDate(person.deathDate)}`} />}
  </div>
  <div className={styles.info}>
    <div className={styles.name}>{person.name}</div>
    <div className={styles.positions}>{person.roles.join(', ')}</div>
    <div className={styles.dates}>{formatDateRange(person.birthDate, person.deathDate)}</div>
  </div>
</div>
```

```css
.poster { position: relative; }
.deceasedBar {
  position: absolute;
  bottom: 0; right: 0;
  width: 56%; height: 14%;
  background: rgba(0, 0, 0, 0.82);
  transform-origin: bottom right;
  transform: rotate(-8deg) translate(8%, 8%);
  clip-path: polygon(0 40%, 100% 0, 100% 100%, 0 100%);
}
.info { padding: 6px 8px; }         /* the "taller" part — a text footer below the image,
                                        not a different image aspect ratio; PosterImage's
                                        object-fit: contain / natural sizing is untouched */
.name { font-weight: 600; }
.positions { font-size: 0.85em; color: var(--text-muted); }
.dates { font-size: 0.8em; color: var(--text-muted); }
```
The diagonal bar is a single clipped, rotated div anchored to the poster's bottom-right corner —
no image asset needed, themes automatically via the existing CSS custom-property system (a solid
dark bar reads correctly in both light/dark themes without a separate dark-mode override, so no
`prefers-color-scheme` branch is needed here specifically). "A little taller" is satisfied by the
fixed-height text footer under the image, not by changing the headshot's own aspect ratio — the
existing `PosterImage`/`object-fit: contain` behavior is left as-is, consistent with how it
already handles non-2:3 images today.

**Search:** confirmed free — global search already covers all `media_items` with no type filter.
The one cosmetic change is `GlobalSearch.tsx`'s type-label rendering (`item.mediaTypeName`,
line 115) — display `"Person"` instead of the raw `"people"` string for this one type, matching
whatever display-name convention other types already use there.

---

## 6. Frontend — Person Detail Page

**`PersonDetailPage`** reuses `MediaDetailPage`'s structural shell (hero/poster area, `PluginFold`-
style collapsible per-plugin metadata boxes, data-fetching-via-React-Query pattern) but is a
separate component — dropping everything that doesn't apply (change-type, merge, file-scanner
box, library-status section, children grid) and replacing the children-grid section with the
role-grouped credits view:

- Hero: headshot (with the same override lightbox as any other poster, per Section 1.5), name,
  positions, birth/death dates, deceased indicator if applicable.
- Wikipedia's full bio: **the existing generic `PluginMetadataBox`, unmodified** — once
  `PLUGIN_WIKIPEDIA_V4.md`'s people-type enrichment is live, this box already renders
  `ExtendedData.sections` (structurally, per the existing `JsonTree` nested-object renderer) and
  the article images. This directly satisfies "detail page should include the full information
  received from Wikipedia" with no new frontend code — it's the same box every other plugin
  already gets.
- **Credits, grouped by role:** new section, new endpoint —
  `GET /api/v1/people/{id}/credits` returns `media_credits` rows for that
  `person_media_item_id`, joined to their `MediaItem` (name, poster, year, media type) for card
  rendering, grouped server-side by `Role`/`Job`. Rendered as one collapsible sub-section per
  distinct role (Actor, Executive Producer, Narrator, Composer, ...), each a small poster-grid of
  the credited titles (reusing the existing generic poster-card rendering used elsewhere, not a
  new card component). This is the literal Anson Mount example: an "Actor" section listing
  *Strange New Worlds* and *Hell on Wheels* (and any movie he acted in), a separate "Executive
  Producer" section listing whatever he produced — driven directly by `Role`/`Job` grouping over
  `media_credits`, which is precisely the data model Section 1.2/3 exists to make queryable.

---

## 7. Kodi Integration

Confirmed: zero actor-art concept exists today — `CastMemberDto` has no image field, the NFO
writer's `add_actors()` emits no `<thumb>`, even though Kodi's own actor NFO schema supports it
natively. This extends the existing REST-response pattern (Section 1, pattern #1 from the prior
research) — no file-writing workaround, since no local-file-precedence problem has been observed
for actor thumbs the way it has for movie/set posters.

- `ScraperDTOs.cs`: `CastMemberDto(string Name, string? Role, string? ThumbUrl)` — one new field.
- `ScraperController.cs`: when building a movie/show's cast list, resolve each credited person's
  headshot the same way the detail page would (`_overrides.poster_url` else most-recent
  `person_headshots` row, per Section 1.5) and populate `ThumbUrl`.
- `Chronicle_Scraper/lib/nfo_common.py` `add_actors()`: one new line emitting
  `<thumb>{escaped_url}</thumb>` inside each `<actor>` block, alongside the existing
  `<name>`/`<role>`/`<order>`.

No addon-side Python restructuring needed beyond that one line — Kodi's own scraper protocol
already knows how to consume `<actor><thumb>`, it's just never been supplied.

---

## 8. New/Changed Endpoints Summary

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/people` | Catalog-wide people list for `PeopleLibraryPage` — filter/sort params per Section 5. |
| `GET /api/v1/people/{id}/credits` | Role-grouped credits for the detail page (Section 6). |
| `GET /api/v1/people/{id}` | Reuses the existing generic `GET /api/v1/media/{id}` — a person is a `MediaItem`, no separate detail endpoint needed. |
| `PUT/DELETE /api/v1/media/{id}/overrides/{field}` | Unchanged, already generic — works for a person's `poster_url` override with zero backend changes. |
| `MediaItemDto.personHeadshots[]` | New field on the existing item DTO, feeding the client-side image-slot aggregation (Section 1.5). |

---

## 9. Phasing Plan

Dependency-ordered; each phase is independently shippable and testable before the next starts.

1. **Core data model** (Chronicle repo only): `people` media type registration (flip Wikipedia's
   `DisplayName`), `media_credits.person_media_item_id` migration + backfill, `birth_date`/
   `death_date` canonical fields + column promotion, `person_headshots` table,
   `CastMember`/`CrewMember` model change, `PersonResolutionService`, the new
   `MetadataEnrichmentService` credit-write hook. No UI yet — verifiable via direct DB inspection
   and the existing Wikipedia people-type search/fetch already designed.
2. **TMDB extension** (`Chronicle.Plugin.TMDB`): ID + `profile_path` threading (Section 4.1) —
   lowest risk, unblocks real movie/TV credit data immediately once Phase 1 lands.
3. **MusicBrainz extension, free part** (`Chronicle.Plugin.MusicBrainz`): band-member + work-credit
   MBID threading (Section 4.2, first half).
4. **Hardcover extension** (`Chronicle.Plugin.Hardcover`): author ID threading now; narrator
   support blocked on the live-schema check (Section 10) — may land in this phase or slip to a
   follow-on depending on what that check finds.
5. **Frontend** (Chronicle.Web): nav, `PeopleLibraryPage`, `PersonCard`, `PersonDetailPage`,
   credits-grouped-by-role section, `GlobalSearch` label tweak. Fully buildable/demoable once
   Phase 1 + at least Phase 2 have real data flowing.
6. **Kodi wiring** (`Chronicle_Scraper`): `CastMemberDto.ThumbUrl` + NFO `<thumb>` line — small,
   independent, can land any time after Phase 1's headshot resolution exists.
7. **Follow-on (not scoped here):** MusicBrainz recording/release-level production credits
   (Section 4.2, second half) — deferred because it needs new relation-type-to-role mapping
   design, not just wiring.

---

## 10. Open Implementation Items

- **Hardcover narrator schema** — genuinely unresolved by this design; needs a live GraphQL
  schema check against Hardcover's current API before Phase 4 can include narrator credits
  (Section 4.3).
- **Name-collision resolution risk** — accepted, documented limitation of name-only fallback
  matching in `PersonResolutionService` (Section 3) and Wikipedia's own people-type search
  (`PLUGIN_WIKIPEDIA_V4.md` Section 13); mitigated but not eliminated by preferring ID-based
  resolution wherever available.
- **MusicBrainz release/recording-level production credits** — explicitly deferred (Section 9,
  item 7) pending relation-type-to-role mapping design.
- **`GET /api/v1/people` filter/sort exact query shape** — sketched in Section 5, not fully
  specified at the parameter level; straightforward to finalize during Phase 5 implementation
  against whatever the existing generic media-list endpoint already supports, rather than
  designing it blind here.
