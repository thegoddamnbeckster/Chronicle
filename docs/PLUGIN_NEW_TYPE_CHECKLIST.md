# Adding a plugin, or a media type, to Chronicle: everything that has to be true

Written 2026-11-05 after the IGDB plugin shipped with its type present but its pages unchecked: the owner could not
find Video Games on the Metadata Assignment page and the Library, and the plugin's developers were missing from the
game page. Each item below says where it shows up, why it can silently be wrong, and how to check it **in the running
app**. A plugin is not done until every line has been looked at in the browser, not inferred from the code.

## 1. The type

| Check | Why it goes wrong | How to verify |
|---|---|---|
| The plugin declares the type with the same name, display name, verb, unit, labels and cast heading as every other plugin that declares it | Chronicle merges declarations by name at host startup; a mismatch makes the unit depend on load order | `media_types` row in the dev DB; the Plugins page |
| The host was **restarted** after install | Types are created only at host startup | the type exists in `media_types` and is `IsActive` |
| Aliases cover what other plugins and sources call it | A different spelling makes a second type | Add Media type list |

## 2. Metadata Assignment page (Settings)

- Every active type is listed, **sorted by display name, so a type starting with V is at the bottom**. Open the page
  and scroll; also check the "Choose a media type" dropdown at the top.
- A plugin appears on a field only if it **declares** that field, and the name must be one of the host's canonical
  field names (`MetadataResolutionService.FieldMap`: title, overview, year, poster_url, backdrop_url,
  runtime_minutes, rating, genres, cast, crew, tags, logo_url, banner_url, thumb_url, ...). `extended_data` is not
  resolvable; declaring it does nothing.
- **Declare every field the plugin really fills.** IGDB first declared no cast or crew while the type said
  "Developers", so the developers section was empty. A mapper test must assert each declared field is filled, and each
  filled field is declared.
- A **saved assignment config predates the plugin**: the page appends the new plugin to the end of each field. For a
  type where an older plugin is weaker or suspect (IMDb for games; Wikipedia anywhere), the new plugin must be moved
  to the front. Do it, or tell the owner it needs doing; do not assume the default is right.
- Wikipedia goes last in every list.

## 3. Library page

- The Library lists **only types that have at least one item**. A new type is invisible until one exists. Add one
  (Add Media, or an import) and look. If the plugin imports a library, run the import and look.
- The type's display name, hierarchy label and the status labels ("Unwatched", "Watching") must read sensibly for
  the type (a game "Watching" is wrong wording; note it for the UI work).

## 4. Add Media and Fix Match

- The type appears in the Add Media dialog; a search returns results with the plugin's key and cover.
- Fix Match accepts the plugin's id forms; the plugin's `fixMatchHint` says which.

## 5. Plugins page

- The plugin card shows its settings schema, a Save and a Test action, and the scheduled tasks (seeded on install).
- After replacing plugin files on disk (hot deploy) Chronicle refuses to load them until the owner **approves the
  changed files on the Plugins page** (integrity check). A deploy is not finished until it is approved and the version
  shown is the new one.

## 6. Item page

- The cast section is headed with the type's `CastHeading` and is populated; crew is populated.
- The plugin's metadata box shows the plugin's data, including its extended record.
- Images: every image kind the plugin returns (cover, screenshot, artwork ...) maps to a slot in
  `imageExtractor.ts`; an unmapped kind is dropped silently.

## 7. Behaviour under failure (see the IGDB design)

Rate limits pause the queue and burn no retry; rejected credentials cost one call; empty results are cached briefly;
nothing is deleted because a source stopped returning it.

## 8. Packaging and release

- `plugins.json` entry (tags include the type), the embedded fallback copy, `RunTestEnvironment.ps1` build entry,
  `Deploy-Plugin.ps1` entry, `docs/plugins/PLUGIN_*.md`, `docs/PLUGIN_CATALOGUE.md`, `BACKLOG.md`.
- A GitHub release whose zip carries the DLL, deps.json and manifest.
- Version bumped in manifest and csproj together.

## 9. The verification pass (do not skip)

After deploy, in the signed-in browser pane, look at: Plugins page; Metadata Assignment (the type, the fields, the
order); Library (after one item exists); Add Media; one item's detail page (cast, crew, metadata box, images).
Report what was seen, not what the code says.
