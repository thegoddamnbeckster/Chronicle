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
| `provider_family` | `tv`, `movie`, `music` or blank: which family of metadata providers (and Kodi library) serves the type. See below. |
| `is_person_type` | The reference type whose items are the people credited on other media. Found by this flag, not by its name. |
| `is_default_type` | The type new items get when a client does not say what they are. |
| `is_family_default` | The type a provider calls by its family name (the movie family's default is what a provider means by "movie"). |
| `cast_are_actors` | A cast entry on this type is an actor playing a character (shown with the character name). |
| `cast_heading` | The heading for credited people: "Band Members", "Narrators"; blank = "Cast". |
| `aliases` | Other names clients use for the type ("film", "tv_episode", "track"); a client's name resolves to the type that lists it. |
| `import_shape` | How files imported from a sync source become items: `episodic` (show / season / episode), `author-series-work` (author / series / work), `artist-album-track`, or blank. |
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

A new database is given three starting types (TV, movies, music; `BuiltInMediaTypes.Seed`), and a plugin that declares a
type fills in any of the fields above it leaves blank, **once**, when it first creates the type. After that the database
is the truth: renaming a type, or moving the "people" or "default" flag to another type, changes behaviour with no code
involved. No rule in Chronicle is attached to a type's *name*.

## How code reads types

Code that has no database handle asks `MediaTypeRegistry`, a small in-memory snapshot of `media_types` reloaded at start
and whenever a type is created, edited or removed. A name the snapshot has never seen answers "nothing known" (no family,
not the people type), never a guess from how the name is spelled. Plugins describe a type through `MediaTypeSupport`
(`ProviderFamily`, `CastHeading`, `CastAreActors`, `Aliases`, `IsPersonType`, `ImportShape`...).

## Provider family and credits heading

* **Provider family**: a metadata plugin that supports a family is also used for every type in it, so a plugin
  declaring only `tv` serves `anime`. It replaces name-guessing in the scanner and enrichment code. The Plugins catalog
  uses it too (`GET /plugins/catalog?mediaType=anime`). `tv`, `movie` and `music` are the vocabulary providers and Kodi
  speak; a type outside every family is served only by plugins that name it.
* **Heading for credited people** (`cast_heading`): "Band Members" for music, "Narrators" for audiobooks, blank = "Cast".
