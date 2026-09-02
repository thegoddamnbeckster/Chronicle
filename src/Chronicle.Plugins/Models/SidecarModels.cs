using System.Text.Json;

namespace Chronicle.Plugins.Models;

// ── Read side (scan time) ───────────────────────────────────────────────────

/// <summary>
/// The minimum signal Chronicle's own scan-time matching needs from a sidecar file --
/// title/year/season/episode to resolve or create the right MediaItem, plus a primary
/// external id to prefer an exact match over a title+year guess. Deliberately narrower
/// than <see cref="SidecarCapture"/>: this is read on every scanned file (performance-
/// sensitive), while the full capture only needs to happen once, at import time.
/// </summary>
public record SidecarSignal(
    string? Title,
    int? Year,
    int? Season = null,
    int? Episode = null,
    string? ShowTitle = null,
    string? ExternalId = null,
    string? PosterUrl = null,
    /// <summary>Music-type signal -- the old Chronicle.Services.Scan.NfoSignal this record
    /// replaces carried these too, and ScanGroupingService's own music (Artist/Album level-0/
    /// level-1) grouping logic actually reads them. Not every sidecar format has an artist/
    /// album concept (Kodi's own movie/tvshow/episode NFOs never do) -- null there.</summary>
    string? Artist = null,
    string? Album = null);

/// <summary>
/// Full lossless capture of a sidecar file, for storage. RawText is the actual
/// lossless-ingestion guarantee -- the exact bytes read, immune to Parsed ever missing a
/// field the format can carry and to the source file being edited/moved/deleted later.
/// Parsed is a generic structured view (e.g. via Chronicle.Core.Helpers.XmlToJsonConverter
/// for XML-shaped sidecars) kept alongside for display/query convenience -- never a
/// replacement for RawText.
/// </summary>
public record SidecarCapture(string RawText, JsonElement? Parsed);

// ── Write side (build a sidecar from Chronicle's own resolved data) ────────

/// <summary>Base type for a request to build one sidecar document. Never constructed
/// directly -- use <see cref="MovieSidecarBuildRequest"/>, <see cref="ShowSidecarBuildRequest"/>,
/// or <see cref="EpisodeSidecarBuildRequest"/>.</summary>
public abstract record SidecarBuildRequest
{
    /// <summary>
    /// Data ONLY the caller has and Chronicle's server structurally cannot -- e.g. Kodi's
    /// own &lt;fileinfo&gt;&lt;streamdetails&gt; probe of the real file (codec, resolution,
    /// channels, HDR type). Chronicle has no way to obtain this itself: it comes from
    /// actually opening the file, which only the caller's own player/library engine did.
    /// An opaque bag the plugin knows how to splice in by key; Chronicle's server never
    /// inspects or validates its contents. Null/empty when the caller has nothing extra
    /// to add (e.g. a brand-new file Kodi hasn't probed yet).
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? ExtraFields { get; init; }
}

public sealed record MovieSidecarBuildRequest(ResolvedMovieData Data) : SidecarBuildRequest;
public sealed record ShowSidecarBuildRequest(ResolvedShowData Data) : SidecarBuildRequest;
public sealed record EpisodeSidecarBuildRequest(ResolvedEpisodeData Data) : SidecarBuildRequest;

// ── Shared building blocks ──────────────────────────────────────────────────

public record ResolvedRating(double Rating, int? Votes);

/// <summary>One candidate image for a given art slot, tagged with the source it came from.</summary>
public record ResolvedArtworkCandidate(string Url, string Source);

/// <summary>Cross-provider identifiers Chronicle has resolved for this item. Any field may
/// be null if no configured metadata provider currently supplies it.</summary>
public record ResolvedExternalIds(string? Imdb, string? Tvdb, string? Tmdb, string? Trakt);

/// <summary>The movie-set (collection) a movie belongs to, if any.</summary>
public record ResolvedCollection(
    string Name,
    string? Overview = null,
    string? PosterUrl = null,
    string? BackdropUrl = null,
    string? LogoUrl = null,
    string? BannerUrl = null,
    string? ClearartUrl = null,
    string? DiscUrl = null,
    string? ThumbUrl = null);

/// <summary>A season container belonging to a show.</summary>
public record ResolvedSeason(int Number, string? Name, string? PosterUrl);

// ── Per-kind resolved data ──────────────────────────────────────────────────

public record ResolvedMovieData(
    string? Title,
    string? Overview,
    string? Tagline,
    int? Year,
    string? Premiered,
    string? Mpaa,
    string? Country,
    string? Studio,
    int? RuntimeMinutes,
    List<string>? Genres,
    List<CastMember>? Cast,
    List<CrewMember>? Crew,
    List<string>? Tags,
    Dictionary<string, ResolvedRating>? Ratings,
    string? TrailerUrl,
    ResolvedExternalIds? ExternalIds,
    Dictionary<string, List<ResolvedArtworkCandidate>>? Artwork,
    ResolvedCollection? Collection,
    int? UserRating = null,
    double? ResumePositionPercent = null,
    DateTime? ResumeUpdatedAt = null);

public record ResolvedShowData(
    string? Title,
    string? Overview,
    string? Tagline,
    int? Year,
    string? Premiered,
    string? Mpaa,
    string? Country,
    string? Studio,
    string? Status,
    int? RuntimeMinutes,
    List<string>? Genres,
    List<CastMember>? Cast,
    List<CrewMember>? Crew,
    List<string>? Tags,
    Dictionary<string, ResolvedRating>? Ratings,
    string? TrailerUrl,
    ResolvedExternalIds? ExternalIds,
    Dictionary<string, List<ResolvedArtworkCandidate>>? Artwork,
    List<ResolvedSeason>? Seasons,
    int? UserRating = null);

public record ResolvedEpisodeData(
    string? Title,
    string? Overview,
    int Season,
    int Episode,
    int? Year,
    string? Aired,
    int? RuntimeMinutes,
    List<CastMember>? Cast,
    List<CrewMember>? Crew,
    Dictionary<string, ResolvedRating>? Ratings,
    string? ThumbUrl,
    ResolvedExternalIds? ExternalIds,
    string? ShowTitle,
    int? ShowYear,
    int? UserRating = null,
    double? ResumePositionPercent = null,
    DateTime? ResumeUpdatedAt = null);
