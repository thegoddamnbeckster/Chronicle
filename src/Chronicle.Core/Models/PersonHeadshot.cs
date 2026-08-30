namespace Chronicle.Core.Models;

/// <summary>
/// One accumulated headshot for a "people"-type MediaItem. Unlike the normal artwork system
/// (entirely fetch-and-replace -- every plugin's AdditionalImages list is overwritten wholesale
/// on each enrichment run), rows here are inserted and never overwritten or pruned: "most recent"
/// means most-recently-discovered-by-Chronicle, not the photo's real-world capture date, which no
/// provider reliably exposes. Fed by two paths -- a person's own enrichment (PosterUrl from a
/// people-type provider like Wikipedia), and credit resolution on someone else's title (a cast/
/// crew entry's ProfileImageUrl, e.g. TMDB's profile_path) -- both writing into this same table.
/// See docs/plans/2026-08-28-people-section-design.md Section 1.5.
/// </summary>
public class PersonHeadshot
{
    public int Id { get; set; }
    public int PersonMediaItemId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }

    /// <summary>Plugin id that supplied this URL (e.g. "chronicle.plugin.tmdb") -- provenance
    /// is about who supplied the URL, not necessarily who the image depicts (a credit-path
    /// headshot is tagged with the TITLE's enriching plugin, even though the image is of the
    /// person).</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public MediaItem PersonMediaItem { get; set; } = null!;
}
