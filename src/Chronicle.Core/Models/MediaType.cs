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

        /// <summary>
        /// JSON describing what files of this type look like, used by the scanner to notice a folder that was
        /// scanned as the wrong type: <c>{"filePatterns":[regex],"folderPatterns":[regex],"extensions":[".mkv"]}</c>.
        /// Patterns are distinctive (an S01E02 name); extensions are broad. Null = no hints, never flagged.
        /// </summary>
        public string? ScanHintsJson { get; set; }

        /// <summary>
        /// Which family of metadata providers also serves this type: "tv", "movie" or "music" (free text, lowercase).
        /// A provider that declares "tv" support is then used for an "anime" item, one declaring "movie" for fan edits.
        /// Null = only providers naming this exact type. Replaces guessing from the type's NAME.
        /// </summary>
        public string? ProviderFamily { get; set; }

        /// <summary>Heading for the people credited on an item of this type ("Cast", "Band Members", "Narrators").
        /// Null = "Cast".</summary>
        public string? CastHeading { get; set; }

        /// <summary>
        /// True for the reference type whose items are people credited on other media. Chronicle finds that type by this flag,
        /// never by its name, so it can be renamed. At most one type should carry it.
        /// </summary>
        public bool IsPersonType { get; set; }

        /// <summary>
        /// True for the one type used when a client (an import, a scrobbler, a scan) does not say what kind of item it is
        /// talking about. At most one type should carry it.
        /// </summary>
        public bool IsDefaultType { get; set; }

        /// <summary>
        /// True when this is the type new items are created under when only a <see cref="ProviderFamily"/> is known (for
        /// example Kodi's movie scraper adding a film). At most one type per family should carry it. The type's name in the
        /// provider vocabulary is then the family itself ("movie"), which is how a plugin declaring "movie" reaches "movies".
        /// </summary>
        public bool IsFamilyDefault { get; set; }

        /// <summary>
        /// True when a cast entry for this type is an actor playing a named character (the credit role is "Actor" and the
        /// character name is kept), false when the entry's own role IS the contribution (an author, a narrator, a band member).
        /// </summary>
        public bool CastAreActors { get; set; }

        /// <summary>
        /// Other names clients and plugins use for this type, comma-separated and lowercase ("movie,film" for movies,
        /// "tv_show,tv_episode,show,episode" for TV). A name that is itself a type's name always wins over an alias.
        /// </summary>
        public string? Aliases { get; set; }

        /// <summary>
        /// How plays imported from other services (and scrobbles) are filed under this type: see <see cref="ImportShapes"/>.
        /// Null = each play is a single flat item. Set here, in the database, rather than recognised by the type's name.
        /// </summary>
        public string? ImportShape { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    /// <summary>The values <see cref="MediaType.ImportShape"/> may take: each names a hierarchy the importer knows how to build.</summary>
    public static class ImportShapes
    {
        /// <summary>Show, season, episode: a play with season and episode numbers lands on the episode under its show.</summary>
        public const string Episodic = "episodic";
        /// <summary>Author, series, book: a play with an author lands on the book under its author (and series).</summary>
        public const string AuthorSeriesWork = "author-series-work";
        /// <summary>Artist, album, track: a play with an artist lands on the track under its album and artist.</summary>
        public const string ArtistAlbumTrack = "artist-album-track";

        public static readonly string[] All = [Episodic, AuthorSeriesWork, ArtistAlbumTrack];
        public static bool IsKnown(string? s) => s is null || All.Contains(s);
    }

    /// <summary>The values <see cref="MediaType.ScanStrategy"/> may take.</summary>
    public static class ScanStrategies
    {
        public const string Audiobook = "audiobook";
        public static readonly string[] All = [Audiobook];
        public static bool IsKnown(string? s) => s is null || All.Contains(s);
    }
}

namespace Chronicle.Core.Models
{
    /// <summary>
    /// The provider-family vocabulary shared with metadata providers and Kodi: a plugin that declares "movie" or "tv" support,
    /// and Kodi's own movie and TV scrapers, speak in these. Which media type belongs to which family is data
    /// (<see cref="MediaType.ProviderFamily"/>), never decided from a type's name.
    /// </summary>
    public static class ProviderFamilies
    {
        public const string Movie = "movie";
        public const string Tv = "tv";
        public const string Music = "music";
    }
}
