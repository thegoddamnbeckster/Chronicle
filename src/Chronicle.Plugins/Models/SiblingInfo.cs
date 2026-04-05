namespace Chronicle.Plugins.Models;

/// <summary>
/// Structured metadata for one sibling or child item, used in Stage 4 sub-item
/// metadata comparison. Fields are populated progressively: filename/path first,
/// then duration, then full tags — only as much as needed to build confidence.
/// </summary>
public record SiblingInfo(
    /// <summary>Normalised display name (tag title or filename stem).</summary>
    string Name,
    /// <summary>Track number, episode number, etc. — from filename prefix or tag.</summary>
    int?   ItemNumber      = null,
    /// <summary>Disc or season number — from folder path or tag.</summary>
    int?   DiscNumber      = null,
    /// <summary>Duration in whole seconds. Match tolerance is configurable (default ±10 s).</summary>
    int?   DurationSeconds = null,
    /// <summary>
    /// Additional tag fields keyed by lowercase tag name (e.g. "isrc", "genre").
    /// Populated only in Tier 3 (full tag read). Null when Tier 3 was not reached.
    /// </summary>
    IReadOnlyDictionary<string, string>? Tags = null
);
