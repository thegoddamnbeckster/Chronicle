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
    Task<Plugin> InstallPluginAsync(string dllPath, CancellationToken ct = default, string? catalogPluginId = null);

    /// <summary>Records the plugin's current files as the trusted ones (used after an update through Chronicle and
    /// when an administrator approves a change made by hand). Also lets a blocked plugin load again.</summary>
    Task AcceptCurrentFilesAsync(string pluginId, CancellationToken ct = default);

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
    /// The one process for removing a plugin, always followed: records the removal, unloads the plugin, removes everything it put
    /// in Chronicle (enrichment rows, external ids, credits, headshots, person credits, background tasks, notifications, its data
    /// on every item with the resolved values recomputed, and its entries in the settings that name plugins), then its record,
    /// and last its own folder. Safe to run again after an interruption (see <see cref="CompleteRemovalAsync"/>).
    /// </summary>
    Task UninstallPluginAsync(int id);

    /// <summary>
    /// Finishes a removal that was begun and not completed (a marker exists in app_settings), or removes the leftovers of a plugin
    /// whose record is already gone. Idempotent: each step removes only what is still there.
    /// </summary>
    Task CompleteRemovalAsync(string pluginId, CancellationToken ct = default);

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
    /// Call after overwriting an installed plugin's DLL with a newer version and reloading
    /// it (see PluginsController.UpdateFromCatalog): re-reads the now-loaded manifest's
    /// Version onto the DB row, clears LatestVersionAvailable (the update this was flagging
    /// just happened), and stamps UpdateCheckedAt/UpdatedAt.
    /// </summary>
    Task<Chronicle.Core.Models.Plugin> MarkUpdatedAsync(string pluginId, CancellationToken ct = default);

    /// <summary>
    /// Runs the health check for the loaded plugin matching the given database id.
    /// Returns null if the plugin is not loaded or exposes no checkable provider.
    /// The result includes an optional failure reason and a severity flag so the
    /// UI can distinguish configuration issues (yellow) from hard failures (red).
    /// </summary>
    Task<PluginHealthResult?> HealthCheckAsync(int id, CancellationToken ct = default);
}
