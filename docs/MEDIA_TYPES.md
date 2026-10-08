# Media types

A media type (movies, TV, music, audiobooks, comics...) is a row in `media_types`. Chronicle contains no code that is
specific to one type: a type describes itself, and the rest of the application reads that description.

## What a type says about itself

| Field | Used for |
|---|---|
| `name` | Internal key. Plugins refer to it; it never changes. |
| `display_name`, `description` | Shown to people. |
| `hierarchy_levels`, `hierarchy_labels` | How many levels items nest (1 = flat, 3 = Show/Season/Episode) and what each is called. The detail page's "Seasons" / "Tracks" headings come from here. |
| `interaction_verb` | The action word, past tense: `watched`, `listened`, `read`, `played`, or your own. Statuses on an item read from it ("Plan to Listen", "Reading", "Re-watching"). A word the interface has no grammar for gets neutral wording ("Planned", "In progress"). |
| `progress_unit` | minutes, pages, tracks... |
| `supports_collections` | The top level is a bucket of separate works (a movie collection). |
| `is_trackable` | Whether items appear in each person's library (reference types such as "people" are not). |
| `scan_strategy` | How the file scanner groups files: blank = by number of levels, `audiobook` = one book per folder. |
| `is_user_modified` | Set when an administrator edits the type; plugins then stop re-describing it. |

Screens that mix every type (the library filter, the dashboard, history, reports) use wording with no action word
("In progress", "Time spent") because "Watching" would be wrong for music.

## Where types come from

1. **Plugins.** A plugin declares the types it handles (`MediaTypeSupport`); at start Chronicle creates any that are
   missing and keeps plugin-managed ones in line with the declaration.
2. **Administrators.** Settings -> Media Types adds a type of your own (a plugin for it can be installed later), edits any
   type, switches one off, deletes an empty custom one, or hands an edited type back to its plugins. Editing marks the
   type as yours, so a plugin update does not undo it. The internal name never changes, and the number of levels is
   fixed while the type holds items.

The page also shows, for each type, how many items it holds and which installed plugins handle it. A type with no plugin
and no items says so and links to the plugin catalogue.

## Starting values

`ScanStrategies.DefaultFor` maps the name `audiobooks` to the `audiobook` strategy, **once**, when a plugin first creates
that type (and a migration carried the old behaviour over for existing databases). After that the database is the truth.

## Provider family and credits heading

Two more per-type settings, both editable on Settings -> Media Types (and seeded once from the type's name, after which the
database is the truth):

* **Provider family** (`media_types.ProviderFamily`: `tv`, `movie`, `music` or blank). A metadata plugin that supports a
  family is also used for every type in it, so a plugin declaring only `tv` serves `anime`. It replaces name-guessing in
  the scanner and enrichment code. Code that has no database handle reads it through `MediaTypeFamilies`, an in-memory
  snapshot reloaded at start and whenever a type is created, edited or removed. The Plugins catalog uses it too
  (`GET /plugins/catalog?mediaType=anime`).
* **Heading for credited people** (`media_types.CastHeading`): "Band Members" for music, "Narrators" for audiobooks,
  blank = "Cast".
