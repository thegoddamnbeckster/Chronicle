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
