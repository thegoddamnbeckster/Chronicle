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
    string? PreciseName      = null
);
