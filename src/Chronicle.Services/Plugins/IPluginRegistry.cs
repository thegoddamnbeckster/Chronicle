using Chronicle.Plugins;

namespace Chronicle.Services.Plugins;

/// <summary>
/// Manages the in-process set of loaded plugin assemblies.
/// Use this to discover available providers and widgets at runtime.
/// </summary>
public interface IPluginRegistry
{
    /// <summary>Returns all loaded <see cref="IMetadataProvider"/> instances across all plugins.</summary>
    IReadOnlyList<IMetadataProvider> GetMetadataProviders();

    /// <summary>
    /// Returns the <see cref="IMetadataProvider"/> whose <c>PluginId</c> matches, or <c>null</c>.
    /// </summary>
    IMetadataProvider? GetMetadataProvider(string pluginId);

    /// <summary>Returns all loaded <see cref="IWidgetPlugin"/> instances across all plugins.</summary>
    IReadOnlyList<IWidgetPlugin> GetWidgetPlugins();

    /// <summary>Returns all loaded <see cref="IImportProvider"/> instances across all plugins.</summary>
    IReadOnlyList<IImportProvider> GetImportProviders();

    /// <summary>
    /// Returns the <see cref="IImportProvider"/> whose <c>PluginId</c> matches, or <c>null</c>.
    /// </summary>
    IImportProvider? GetImportProvider(string pluginId);

    /// <summary>Returns all currently loaded plugins.</summary>
    IReadOnlyList<LoadedPlugin> GetLoadedPlugins();

    /// <summary>
    /// Loads a plugin assembly from <paramref name="dllPath"/> using an isolated
    /// <see cref="PluginLoadContext"/>, applies stored settings, and registers all
    /// discovered interfaces.
    /// </summary>
    /// <param name="dbId">Database record id for the plugin (used to track identity).</param>
    /// <param name="settings">Key-value settings read from the database to pass to <see cref="IMetadataProvider.Configure"/>.</param>
    Task<LoadedPlugin> LoadPluginAsync(
        int dbId,
        string dllPath,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken ct = default);

    /// <summary>Unloads the plugin identified by its database id.</summary>
    void UnloadPlugin(int dbId);
}
