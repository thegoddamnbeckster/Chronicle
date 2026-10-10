namespace Chronicle.Plugins.Models;

/// <summary>Describes which media type a plugin supports and what fields it provides.</summary>
public class MediaTypeSupport
{
    /// <summary>Media type name as stored in the database, e.g. "movies", "tv", "music".</summary>
    public string MediaTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name, e.g. "Movies", "TV", "Fan Edits".
    /// When non-empty, Chronicle will upsert this type into the media_types table at startup
    /// so media types are always derived from installed plugins.
    /// Leave empty for internal aliases (e.g. "movie" as a legacy alias for "movies") that
    /// should not create a standalone DB entry.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Number of hierarchy levels for this type.
    /// 1 = flat (movies, fan edits, audiobooks).
    /// 3 = three-tier (TV: Show/Season/Episode, Music: Artist/Album/Track).
    /// </summary>
    public int HierarchyLevels { get; set; } = 1;

    /// <summary>
    /// Labels for each hierarchy level, e.g. ["Show", "Season", "Episode"] for TV.
    /// Length must equal <see cref="HierarchyLevels"/>. Null for flat types.
    /// </summary>
    public string[]? HierarchyLabels { get; set; }

    /// <summary>Verb used for user interaction tracking, e.g. "watched", "listened", "read".</summary>
    public string InteractionVerb { get; set; } = "watched";

    /// <summary>Unit of progress tracking, e.g. "minutes", "pages", "percent".</summary>
    public string ProgressUnit { get; set; } = "minutes";

    /// <summary>
    /// True when this plugin should enrich items of this type (keep its ids on them, appear in their
    /// Metadata Assignment, run its own enrichment pass) but must NOT take part in finding/identifying
    /// them -- i.e. it is left out of Add Media and file-scan identity search. For types another
    /// plugin owns the identity of: Fan Edits are identified by the FanEdit plugin alone, and a generic
    /// movie database that also searched them would only return the unedited original film. Defaults
    /// to false, so every existing declaration keeps taking part in search.
    /// </summary>
    public bool EnrichOnly { get; set; }

    /// <summary>Lower numbers = higher priority when multiple providers support the same type.</summary>
    public int DefaultPriority { get; set; } = 10;

    /// <summary>
    /// False for a reference/catalog type whose items are only ever pointed at by other media
    /// (e.g. "people" -- credited on movies/shows/albums, never watched or listened to on its
    /// own). Chronicle's library auto-tracking (every root item a user hasn't interacted with
    /// gets an implicit Unwatched row) skips these. True for every ordinary trackable type.
    /// </summary>
    public bool IsTrackable { get; set; } = true;

    /// <summary>
    /// Optional. How the file scanner should group files of this type ("audiobook"); null lets Chronicle
    /// decide from the hierarchy. Only used when Chronicle first creates the type - after that the value in the
    /// Media Types page is what counts.
    /// </summary>
    public string? ScanStrategy { get; set; }

    /// <summary>Metadata fields this plugin can populate for the root level (level 0) of this type.</summary>
    public List<string> SupportedFields { get; set; } = [];

    /// <summary>
    /// Per-level field overrides for hierarchical types.
    /// Key = level index (1 = first sub-level, 2 = second sub-level, …).
    /// If a level is absent, Chronicle derives a default field set from the hierarchy position.
    /// Only relevant when <see cref="HierarchyLevels"/> &gt; 1.
    /// </summary>
    public Dictionary<int, List<string>>? LevelFields { get; set; }
}
