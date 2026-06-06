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

    /// <summary>
    /// Decrypts the plugin's existing settings, merges <paramref name="newSettings"/> on top,
    /// then re-encrypts and persists the result. Used by the OAuth callback path to add tokens
    /// without discarding the user-configured client credentials.
    /// </summary>
    Task MergeSettingsAsync(string pluginId, IReadOnlyDictionary<string, string> newSettings, CancellationToken ct = default);

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
    /// Unloads the plugin assembly from the registry, releasing the file lock on its DLL,
    /// without changing its enabled/disabled state in the database.
    /// Call this before overwriting the DLL on disk, then call ReloadPluginAsync.
    /// Returns true if the plugin was loaded and has been unloaded; false if it was not loaded.
    /// </summary>
    Task<bool> UnloadFromRegistryAsync(string pluginId);

    /// <summary>
    /// Reloads the plugin from its registered DLL path on disk.
    /// The plugin must be enabled in the database. Safe to call after UnloadFromRegistryAsync.
    /// </summary>
    Task ReloadPluginAsync(string pluginId, CancellationToken ct = default);

    /// <summary>
    /// Runs the health check for the loaded plugin matching the given database id.
    /// Returns null if the plugin is not loaded or exposes no checkable provider.
    /// The result includes an optional failure reason and a severity flag so the
    /// UI can distinguish configuration issues (yellow) from hard failures (red).
    /// </summary>
    Task<PluginHealthResult?> HealthCheckAsync(int id, CancellationToken ct = default);
}
