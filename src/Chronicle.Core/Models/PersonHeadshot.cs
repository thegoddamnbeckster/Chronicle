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

    /// <summary>Short-form plugin id that supplied this URL (e.g. "tmdb", via
    /// PluginIdHelper.ToSource -- same convention as MediaExternalId.Source/MediaCredit.Source)
    /// -- provenance is about who supplied the URL, not necessarily who the image depicts (a
    /// credit-path headshot is tagged with the TITLE's enriching plugin, even though the image
    /// is of the person).</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The user hid this photo (it is of someone else, or simply wrong - TMDB's gallery for a person can carry a
    /// stranger's picture). The row is kept, not deleted: rows are insert-only and re-enrichment skips what it has
    /// already seen, so a deleted photo would be re-added at the next refresh. A hidden photo is not shown in the
    /// gallery, never chosen as the person's current photo, and not used to match duplicate people.
    /// </summary>
    public bool IsHidden { get; set; }

    // Navigation
    public MediaItem PersonMediaItem { get; set; } = null!;
}
