using Chronicle.Plugins.Models;

namespace Chronicle.Plugins;

/// <summary>
/// Implemented by plugins that scan local file system directories and discover media files.
/// All implementations must be stateless between calls.
/// </summary>
public interface IFileScannerPlugin
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Unique reverse-domain plugin identifier, e.g. "chronicle.plugin.filescanner".</summary>
    string PluginId { get; }

    string Name    { get; }
    string Version { get; }
    string Author  { get; }
    string Description { get; }

    // ── Capability declarations ───────────────────────────────────────────────

    /// <summary>Returns the media types this scanner can discover (e.g. "movies", "tv").</summary>
    MediaTypeSupport[] GetSupportedMediaTypes();

    /// <summary>Returns the settings schema used to generate the configuration UI.</summary>
    PluginSettingsSchema GetSettingsSchema();

    /// <summary>
    /// Returns the minimum confidence score (0–100) required for a grouped result to be
    /// auto-imported by the scheduled scan task for the given <paramref name="mediaTypeName"/>.
    /// Implementations that support per-type thresholds should override this.
    /// The default falls back to <see cref="ConfidenceThreshold"/>.
    /// </summary>
    int GetConfidenceThreshold(string mediaTypeName) => ConfidenceThreshold;

    /// <summary>
    /// Default/fallback confidence threshold (0–100) when no per-type value is configured.
    /// Configured via the plugin settings schema.
    /// </summary>
    int ConfidenceThreshold => 75;

    /// <summary>
    /// Maximum number of folders to scan concurrently during a scheduled scan.
    /// Returns 0 when unconfigured, which means "auto" (max(1, CPU cores / 4)).
    /// Configured via the plugin settings schema key <c>max_concurrency</c>.
    /// </summary>
    int MaxConcurrency => 0;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called once after instantiation with the persisted settings.
    /// Keys match <see cref="SettingDefinition.Key"/> values from the schema.
    /// </summary>
    void Configure(IReadOnlyDictionary<string, string> settings);

    // ── Core operation ────────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="path"/> and returns all discovered media files with parsed metadata.
    /// </summary>
    /// <param name="path">Root directory to scan.</param>
    /// <param name="recursive">Whether to recurse into sub-directories.</param>
    Task<List<ScannedFile>> ScanDirectoryAsync(
        string path,
        bool recursive,
        CancellationToken ct = default);

    /// <summary>Verifies that the scanner can access the underlying file system.</summary>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
}
