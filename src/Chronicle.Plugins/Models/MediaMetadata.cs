using System.Text.Json;
using System.Text.Json.Serialization;

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

    // ── Artwork fields (supplementary providers e.g. Fanart.tv) ──────────────

    /// <summary>Transparent PNG logo (HD: ~800×310, SD: ~400×155).</summary>
    public string? LogoUrl { get; set; }
    /// <summary>Wide banner image (~1000×185).</summary>
    public string? BannerUrl { get; set; }
    /// <summary>Landscape thumb / still (~1000×562).</summary>
    public string? ThumbUrl { get; set; }
    /// <summary>Transparent character/person art PNG.</summary>
    public string? ClearartUrl { get; set; }
    /// <summary>Circular disc / CD art image (~1000×1000).</summary>
    public string? DiscUrl { get; set; }
    /// <summary>Character art PNG (TV shows, ~512×512).</summary>
    public string? CharacterArtUrl { get; set; }

    // ── Extended fields stored in MetadataJson ────────────────────────────────

    public List<string> Genres { get; set; } = [];
    public List<CastMember> Cast { get; set; } = [];
    /// <summary>Every non-actor credit the provider supplies (director, writer, producer,
    /// executive producer, composer, etc.) -- not just directors. Job is null when the
    /// provider doesn't break its credits down by title.</summary>
    public List<CrewMember> Crew { get; set; } = [];
    public double? Rating { get; set; }

    /// <summary>Community/folksonomy tags beyond the curated Genres list (e.g. MusicBrainz tags).</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Alternate names for this item — pen names, name variants, alias titles.
    /// Populated primarily for author-level items (e.g. "K. J. Parker" → "Tom Holt").
    /// Stored in metadata_json; the enrichment service uses these as additional search
    /// terms when attempting to find a match for items that share this name.
    /// </summary>
    public List<string> AlternateNames { get; set; } = [];

    /// <summary>
    /// All images returned by the provider beyond the primary PosterUrl/BackdropUrl
    /// (e.g. back cover, booklet, CD tray, episode stills).
    /// </summary>
    public List<AdditionalImage> AdditionalImages { get; set; } = [];

    /// <summary>
    /// Provider-specific structured data that doesn't map to any generic field above
    /// (e.g. track listings, label info, composer credits, ISRCs).
    /// Stored as a raw JSON element to preserve fidelity without boxing overhead.
    /// Null when the provider has nothing extra to store.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ExtendedData { get; set; }

    // ── Search-mode fields (populated when returning a list of results) ────────

    /// <summary>Multiple results returned from a search query. Null when serializing a single item to avoid circular references.</summary>
    public List<MediaMetadata>? Results { get; set; } = [];

    /// <summary>Total number of results available (for pagination display).</summary>
    public int TotalResults { get; set; }
}

/// <summary>An actor credit: the performer's real name and, when the provider supplies it, the
/// character/role they played. Role is null for providers that don't carry that data (e.g. band
/// members from MusicBrainz).</summary>
public record CastMember(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string? Role = null);

/// <summary>A non-actor credit: the person's real name and, when the provider breaks
/// credits down by title, their job (Director, Writer, Producer, Executive Producer,
/// Composer, etc.). Job is null when the provider only supplies a flat name list.</summary>
public record CrewMember(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("job")]  string? Job = null);

/// <summary>
/// One episode as returned by <see cref="IMetadataProvider.GetEpisodeListAsync"/> --
/// deliberately minimal (just enough to build a local Episode item and match it against
/// a file Kodi found by season/episode number), not the full <see cref="MediaMetadata"/>
/// shape a per-episode <c>GetByIdAsync</c> call would return.
/// </summary>
public record ProviderEpisodeSummary(
    int EpisodeNumber,
    string Title,
    string? Overview = null,
    string? StillUrl = null,
    string? AirDate = null);

/// <summary>A single additional image from a metadata provider (back cover, booklet, still, etc.).</summary>
public class AdditionalImage
{
    /// <summary>Full-resolution image URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Image type label as reported by the provider (e.g. "Front", "Back", "Booklet",
    /// "Medium", "Spine", "Obi", "Tray", "Watermark", "Raw/Unedited").
    /// </summary>
    public string? Type { get; set; }

    /// <summary>Thumbnail URL (typically ~500 px) for fast preview rendering.</summary>
    public string? ThumbnailUrl { get; set; }
}
