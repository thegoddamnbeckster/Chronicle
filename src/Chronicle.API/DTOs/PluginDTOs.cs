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
    string? IconUrl = null
);

public record InstallPluginRequest(
    [Required] string DllPath
);

public record UpdatePluginSettingsRequest(
    [Required] Dictionary<string, string> Settings
);

public record PluginHealthDto(bool? Healthy);

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
    string? Sha256 = null
);
