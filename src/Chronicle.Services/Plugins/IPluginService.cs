using Chronicle.Core.Models;

namespace Chronicle.Services.Plugins;

public interface IPluginService
{
    /// <summary>Returns all installed plugins (enabled and disabled).</summary>
    Task<List<Plugin>> GetAllPluginsAsync();

    /// <summary>Returns the plugin with the given database id, or null if not found.</summary>
    Task<Plugin?> GetPluginAsync(int id);

    /// <summary>
    /// Registers a new plugin in the database and loads it into the registry.
    /// Throws <see cref="InvalidOperationException"/> if a plugin with the same PluginId
    /// is already installed.
    /// </summary>
    Task<Plugin> InstallPluginAsync(string dllPath, CancellationToken ct = default);

    /// <summary>Persists settings for the plugin and reconfigures the loaded instance.</summary>
    Task UpdateSettingsAsync(int id, Dictionary<string, string> settings);

    /// <summary>Enables the plugin and loads it into the registry.</summary>
    Task EnablePluginAsync(int id);

    /// <summary>Disables the plugin and unloads it from the registry.</summary>
    Task DisablePluginAsync(int id);

    /// <summary>
    /// Removes the plugin record from the database and unloads it from the registry.
    /// Does NOT delete the DLL from disk.
    /// </summary>
    Task UninstallPluginAsync(int id);

    /// <summary>
    /// Runs the health check for the loaded metadata provider matching the given database id.
    /// Returns null if the plugin is not loaded or has no metadata provider.
    /// </summary>
    Task<bool?> HealthCheckAsync(int id, CancellationToken ct = default);
}
