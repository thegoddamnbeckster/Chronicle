namespace Chronicle.Plugins.Models;

/// <summary>
/// Context passed to <see cref="IMetadataProvider.SearchAsync"/> so the plugin
/// can construct its own query and score candidates without Chronicle knowing
/// provider-specific syntax (Lucene, etc.).
/// </summary>
public record MediaSearchContext(
    /// <summary>Item name, pre-normalised by Chronicle (punctuation stripped, lowercased).</summary>
    string  Name,
    int?    Year              = null,
    /// <summary>Parent item name — artist for an album, show for a season.</summary>
    string? ParentName        = null,
    /// <summary>Grandparent item name — artist for a track.</summary>
    string? GrandparentName   = null,
    /// <summary>Position within parent — season number, track number, episode number.</summary>
    int?    ItemNumber        = null,
    /// <summary>
    /// Number of direct children already in Chronicle for this item.
    /// Allows structural validation: does the provider's season count match?
    /// </summary>
    int?    ChildCount        = null,
    /// <summary>0 = root (show/artist/movie), 1 = season/album, 2 = episode/track.</summary>
    int     HierarchyLevel   = 0,
    /// <summary>
    /// Precise title read directly from file metadata (e.g. NFO &lt;title&gt; element).
    /// When present, plugins use it for an exact case-insensitive comparison against
    /// candidate titles WITHOUT punctuation stripping — so "What If...?" stays distinct
    /// from "What If".  Only set when a reliable file-metadata source is available;
    /// null means fall back to <see cref="Name"/>-based scoring only.
    /// </summary>
    string? PreciseName      = null,
    /// <summary>
    /// Clean title derived from the source file's filename (extension and leading track-number
    /// stripped).  Set only when it differs meaningfully from <see cref="Name"/>.
    /// Plugins use this as a fallback search term when the tag-based <see cref="Name"/> returns
    /// zero results from the provider — e.g. a tag says "Duck and Run (LP version)" but the
    /// filename is "01 - Duck and Run.mp3", giving a stem of "Duck and Run" that matches MusicBrainz.
    /// </summary>
    string? FilenameStem     = null,
    /// <summary>
    /// Names of sibling items that share the same parent (e.g. other tracks on the same album or
    /// single).  Plugins can use these to identify the precise release when the current item's
    /// title alone is ambiguous — e.g. search for a sibling with <c>inc=releases</c> to obtain a
    /// release MBID, then search the current item with <c>reid:{mbid}</c>.
    /// Null or empty when no siblings are available or when the item is not a leaf node.
    /// </summary>
    IReadOnlyList<string>? SiblingNames = null,

    /// <summary>
    /// Ordered list of alternative title forms to try in each search stage:
    /// [PreciseName?, year-stripped name, filename stem?, version-qualifier-stripped?].
    /// Duplicates are removed. Null means the plugin should fall back to <see cref="Name"/>.
    /// </summary>
    IReadOnlyList<string>? AltTitles = null,

    /// <summary>
    /// Names of direct child items for HierarchyLevel 0 (artist → albums) or
    /// HierarchyLevel 1 (album → tracks, show → episodes). Used in Stage 3 to compare
    /// the provider's sub-item list against what Chronicle already has.
    /// Null or empty for leaf items (HierarchyLevel 2) — use <see cref="SiblingNames"/> instead.
    /// </summary>
    IReadOnlyList<string>? ChildNames = null,

    /// <summary>
    /// Structured metadata for sibling items (leaf level) or child items (parent levels).
    /// Used in Stage 4 sub-item metadata comparison against the provider's data.
    /// Populated in tiers: filename/path info first, then duration, then full tags.
    /// </summary>
    IReadOnlyList<SiblingInfo>? SubItemMetadata = null,

    /// <summary>
    /// The Chronicle media type name for this item (e.g. "movies", "tv", "fanedits", "music").
    /// Plugins may use this to restrict their search to the appropriate endpoint —
    /// e.g. TMDB searches only /search/movie for movie-type items and only /search/tv for TV items.
    /// Null means the caller did not provide a type; plugins should search all applicable endpoints.
    /// </summary>
    string? MediaTypeName = null
);
