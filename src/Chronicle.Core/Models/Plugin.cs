namespace Chronicle.Core.Models;

/// <summary>
/// Database record for an installed Chronicle plugin.
/// Settings are stored as JSON (keys match SettingDefinition.Key values from the plugin's schema).
/// </summary>
public class Plugin
{
    public int Id { get; set; }

    /// <summary>Unique reverse-domain identifier, e.g. "chronicle.plugin.tmdb".</summary>
    public string PluginId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Absolute path to the plugin DLL on disk.</summary>
    public string DllPath { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// JSON-serialised dictionary of plugin settings (key → value, both strings).
    /// Sensitive values (passwords, API keys) are stored as-is in Phase 1;
    /// encryption is a planned future enhancement.
    /// </summary>
    public string? SettingsJson { get; set; }

    public DateTime InstalledAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Hex accent colour for light-mode UI (from manifest brandColorLight).</summary>
    public string? BrandColorLight { get; set; }

    /// <summary>Hex accent colour for dark-mode UI (from manifest brandColorDark).</summary>
    public string? BrandColorDark { get; set; }
}
