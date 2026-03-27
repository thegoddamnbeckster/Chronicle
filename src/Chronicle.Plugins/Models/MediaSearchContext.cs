namespace Chronicle.Plugins.Models;

/// <summary>
/// Context passed to <see cref="IMetadataProvider.SearchAsync"/> so the plugin
/// can construct its own query and score candidates without Chronicle knowing
/// provider-specific syntax (Lucene, etc.).
/// </summary>
public record MediaSearchContext(
    /// <summary>Item name, pre-normalised by Chronicle (punctuation stripped, lowercased).</summary>
    string  Name,
    int?    Year,
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
    int     HierarchyLevel   = 0
);
