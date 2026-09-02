using Chronicle.Plugins.Models;

namespace Chronicle.Plugins;

/// <summary>
/// A plugin that owns one local sidecar-metadata format end to end -- both reading it
/// (during a scan, for matching signal and for lossless capture) and writing it (building a
/// document from Chronicle's own resolved data, for an external tool to write to disk).
///
/// Kept as one interface, not two, because round-trip fidelity is the whole point: whatever
/// BuildAsync produces must be exactly what ExtractSignal/CaptureLossless can read back --
/// one implementation owns the schema, not a reader and a writer that could quietly drift.
///
/// Exists so Chronicle's core scan pipeline (BuiltInFileScannerPlugin, ScanGroupingService,
/// FileScanService) never needs to know what a specific sidecar format looks like -- it just
/// asks every installed ISidecarFormatPlugin "does this file have one of yours". No sidecar
/// plugin installed means no sidecar capture at all, the same way no metadata provider
/// installed means no enrichment. See docs/plans/2026-09-02-kodi-nfo-plugin-design.md for
/// the design this implements: Chronicle.Plugin.Kodi.NFO is the first implementation, for
/// Kodi's own .nfo convention.
/// </summary>
public interface ISidecarFormatPlugin
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Unique reverse-domain plugin identifier, e.g. "chronicle.plugin.kodi.nfo".</summary>
    string PluginId { get; }

    string Name    { get; }
    string Version { get; }
    string Author  { get; }

    // ── Capability declarations ───────────────────────────────────────────────

    /// <summary>Returns the media types this plugin's sidecar convention applies to.</summary>
    MediaTypeSupport[] GetSupportedMediaTypes();

    /// <summary>Returns the settings schema used to generate the configuration UI.</summary>
    PluginSettingsSchema GetSettingsSchema();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Called once after instantiation with the persisted settings.</summary>
    void Configure(IReadOnlyDictionary<string, string> settings);

    // ── Read side (scan time) ────────────────────────────────────────────────

    /// <summary>
    /// Given a media file's path, returns the sidecar file this plugin's own convention
    /// says belongs to it, or null if none applies. Chronicle's scan pipeline doesn't know
    /// what a sidecar looks like for any given format -- only the plugin does (e.g. Kodi's
    /// own "prefer &lt;video-stem&gt;.ext, exclude tvshow/season-level files" rule).
    /// </summary>
    string? FindSidecar(string mediaFilePath);

    /// <summary>
    /// Extracts the minimum signal Chronicle's own scan-time matching needs from a sidecar
    /// found via <see cref="FindSidecar"/>. Returns null if the sidecar is missing or
    /// unreadable/unparseable -- never throws.
    /// </summary>
    SidecarSignal? ExtractSignal(string sidecarPath);

    /// <summary>
    /// Full lossless capture of a sidecar for storage -- see <see cref="SidecarCapture"/>.
    /// Returns null if the sidecar is missing or unreadable -- never throws.
    /// </summary>
    SidecarCapture? CaptureLossless(string sidecarPath);

    // ── Write side (on demand, via API) ──────────────────────────────────────

    /// <summary>
    /// Builds a sidecar document for one movie/show/episode from Chronicle's own resolved
    /// data (see <see cref="MovieSidecarBuildRequest"/>/<see cref="ShowSidecarBuildRequest"/>/
    /// <see cref="EpisodeSidecarBuildRequest"/>). Returns the exact bytes to write to disk
    /// (correct encoding/declaration for the format) -- the caller just writes them.
    /// </summary>
    Task<byte[]> BuildAsync(SidecarBuildRequest request, CancellationToken ct = default);
}
