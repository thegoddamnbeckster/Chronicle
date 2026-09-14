using System.Text.Json;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugins;

/// <summary>
/// A plugin that reads a local sidecar-metadata format -- during a scan, for matching signal
/// and for lossless capture, and on demand for the media detail page's "view NFO" panel.
///
/// Exists so Chronicle's core scan pipeline (BuiltInFileScannerPlugin, ScanGroupingService,
/// FileScanService) never needs to know what a specific sidecar format looks like -- it just
/// asks every installed ISidecarFormatPlugin "does this file have one of yours". No sidecar
/// plugin installed means no sidecar capture at all, the same way no metadata provider
/// installed means no enrichment. See docs/plans/2026-09-02-kodi-nfo-plugin-design.md for
/// the design this implements: Chronicle.Plugin.Kodi.NFO is the first implementation, for
/// Kodi's own .nfo convention.
///
/// This interface originally also had a write side (BuildAsync, building a sidecar document
/// from Chronicle's own resolved data for an external tool to write to disk) -- removed
/// 2026-09-13 along with the server-side NFO generation/push/rebuild-queue system that was its
/// only caller. Per-user direction: that system's entire purpose was writing real .nfo files
/// onto the same shares Kodi scans, which made it a standing threat to Kodi ever re-scanning
/// an item, and neither Kodi addon actually requires a local NFO to function. See git history
/// (and Chronicle.Plugin.Kodi.NFO's own history) if the write side is ever needed again.
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

    /// <summary>
    /// Extracts a curated, display-oriented subset of fields from a sidecar -- e.g. plot,
    /// genres, rating for a dedicated UI card -- as opposed to <see cref="CaptureLossless"/>'s
    /// fully generic tree. The exact field set/shape is entirely up to the plugin; Chronicle's
    /// API layer passes the result through verbatim without knowing what's in it. Default
    /// implementation returns null, for a sidecar-format plugin with no curated view worth
    /// giving special UI treatment beyond the generic capture. Never throws.
    /// </summary>
    JsonElement? ExtractCuratedFields(string sidecarPath) => null;
}
