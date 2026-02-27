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
    DateTime UpdatedAt
);

public record InstallPluginRequest(
    [Required] string DllPath
);

public record UpdatePluginSettingsRequest(
    [Required] Dictionary<string, string> Settings
);

public record PluginHealthDto(bool? Healthy);
