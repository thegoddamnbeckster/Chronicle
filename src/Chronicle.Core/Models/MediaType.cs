namespace Chronicle.Core.Models
{
    public class MediaType
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }

        /// <summary>Number of hierarchy levels (e.g. TV = 3: show/season/episode).</summary>
        public int HierarchyLevels { get; set; } = 1;

        /// <summary>Comma-separated labels for each level (e.g. "Show,Season,Episode").</summary>
        public string? HierarchyLabels { get; set; }

        /// <summary>Verb used when the user interacts (e.g. "watched", "listened", "read").</summary>
        public string InteractionVerb { get; set; } = "watched";

        /// <summary>Unit of progress (e.g. "minutes", "pages", "percent").</summary>
        public string ProgressUnit { get; set; } = "minutes";

        public bool IsBuiltIn { get; set; } = false;
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// True when a level-0 item of this type is a bucket of distinct works rather than
        /// one continuous work with sub-parts (a Movie Collection or an Audiobook Author, vs.
        /// a TV Show whose seasons/episodes are chapters of the same thing, not separate
        /// works). Drives whether the library grid shows the item as a browsable "Collection"
        /// card (no status tracking of its own) instead of a normal tracked entry.
        /// </summary>
        public bool SupportsCollections { get; set; } = false;

        /// <summary>
        /// False for a reference/catalog type whose items exist only to be pointed at by other
        /// media (e.g. "people", credited on movies/shows/albums but never watched or listened
        /// to on their own) -- LibraryService.GetForUserAsync's auto-track-every-root-item
        /// mechanism skips these, so they never pick up a spurious per-user status/rating and
        /// never show up as a section in the tracked Library grid. True for every ordinary
        /// trackable type (movies, TV, music, books, ...).
        /// </summary>
        public bool IsTrackable { get; set; } = true;

        /// <summary>
        /// How the file scanner groups this type's files. Null = by hierarchy depth (flat per file, or the folder
        /// tree for 3+ levels). "audiobook" = each book folder is one entry (parts and covers collapse into it),
        /// then an Author/Series/Book tree. Set here, in the database, rather than recognised by the type's NAME,
        /// so a renamed or user-made type behaves the same as the built-in one.
        /// </summary>
        public string? ScanStrategy { get; set; }

        /// <summary>
        /// True once an administrator has edited this type on the Media Types page. Installed plugins declare
        /// the shape of the types they handle and re-assert it at every start; a type marked as user-modified
        /// is left exactly as the administrator set it.
        /// </summary>
        public bool IsUserModified { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    /// <summary>The values <see cref="MediaType.ScanStrategy"/> may take.</summary>
    public static class ScanStrategies
    {
        public const string Audiobook = "audiobook";
        public static readonly string[] All = [Audiobook];
        public static bool IsKnown(string? s) => s is null || All.Contains(s);

        /// <summary>
        /// Starting value for a type created automatically by a plugin that does not say. This is the only place a
        /// type NAME is mapped to a strategy, applied once at creation; after that the database is the truth.
        /// </summary>
        public static string? DefaultFor(string typeName) =>
            string.Equals(typeName, "audiobooks", StringComparison.OrdinalIgnoreCase) ? Audiobook : null;
    }
}
