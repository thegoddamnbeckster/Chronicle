namespace Chronicle.Plugins.Models;

/// <summary>
/// The enriched metadata returned by a metadata provider after a search or id lookup.
/// All fields are optional — providers populate only what they support.
/// </summary>
public class MediaMetadata
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>External ID in the provider's own namespace (e.g. TMDB id "550").</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Source identifier matching the provider (e.g. "tmdb", "musicbrainz").</summary>
    public string Source { get; set; } = string.Empty;

    // ── Core fields ───────────────────────────────────────────────────────────

    public string Title { get; set; } = string.Empty;
    public string? Overview { get; set; }
    public int? Year { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public int? RuntimeMinutes { get; set; }

    // ── Extended fields stored in MetadataJson ────────────────────────────────

    public List<string> Genres { get; set; } = [];
    public List<string> Cast { get; set; } = [];
    public List<string> Directors { get; set; } = [];
    public double? Rating { get; set; }

    // ── Search-mode fields (populated when returning a list of results) ────────

    /// <summary>Multiple results returned from a search query.</summary>
    public List<MediaMetadata> Results { get; set; } = [];

    /// <summary>Total number of results available (for pagination display).</summary>
    public int TotalResults { get; set; }
}
