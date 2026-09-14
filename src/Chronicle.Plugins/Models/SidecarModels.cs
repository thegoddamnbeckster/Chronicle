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

// The write side that used to live here -- SidecarBuildRequest and its Movie/Show/Episode
// subtypes, plus the ResolvedMovieData/ResolvedShowData/ResolvedEpisodeData/ResolvedRating/
// ResolvedArtworkCandidate/ResolvedExternalIds/ResolvedCollection/ResolvedSeason building
// blocks -- was removed 2026-09-13 along with ISidecarFormatPlugin.BuildAsync and the
// server-side NFO generation system that was its only caller. See git history if it's ever
// needed again.
