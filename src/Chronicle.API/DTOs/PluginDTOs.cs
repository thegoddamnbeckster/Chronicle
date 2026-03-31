using System.ComponentModel.DataAnnotations;

namespace Chronicle.API.DTOs;

public record PluginDto(
    int Id,
    string PluginId,
    string Name,
    string Version,
    string Author,
    string? Description,
    bool IsEnabled,
    DateTime InstalledAt,
    DateTime UpdatedAt,
    /// <summary>
    /// Favicon/icon URL from the plugin's manifest.json.
    /// Null when the plugin is not currently loaded or has no iconUrl set.
    /// </summary>
    string? IconUrl = null,
    /// <summary>
    /// Short hint shown in the Fix Match panel. From manifest fixMatchHint.
    /// Null when the plugin has no fixMatchHint in its manifest.
    /// </summary>
    string? FixMatchHint = null,
    /// <summary>
    /// Media type names this plugin can enrich (e.g. ["TV", "Movies"]).
    /// Empty list means the plugin is not loaded or has no providers.
    /// </summary>
    IReadOnlyList<string>? SupportedMediaTypes = null
);

public record InstallPluginRequest(
    [Required] string DllPath
);

public record UpdatePluginSettingsRequest(
    [Required] Dictionary<string, string> Settings
);

/// <param name="Healthy">Whether the plugin passed its health check.</param>
/// <param name="FailureReason">Human-readable reason the check failed. Null when healthy.</param>
/// <param name="IsCritical">
/// True = unexpected failure (red badge). False = configuration/auth issue (yellow badge).
/// </param>
public record PluginHealthDto(bool? Healthy, string? FailureReason = null, bool IsCritical = true);

public record PluginCatalogEntry(
    string PluginId,
    string Name,
    string Description,
    string Author,
    string? IconUrl,
    string GithubRepo,
    string AssetName,
    string DllName,
    string[] Tags,
    bool IsInstalled = false,
    /// <summary>
    /// Expected SHA-256 hex digest of the ZIP asset (lowercase, no prefix).
    /// When set, Chronicle will reject the download if the computed hash does not match,
    /// protecting against a compromised GitHub release or a man-in-the-middle attack.
    /// </summary>
    string? Sha256 = null,
    /// <summary>Version string from the plugin's manifest (e.g. "1.2.0").</summary>
    string Version = ""
);
