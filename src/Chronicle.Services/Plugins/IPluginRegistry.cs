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
    /// Returns all loaded metadata providers paired with their <b>manifest</b> plugin ID.
    /// The manifest plugin ID is the authoritative identifier and may differ from
    /// <see cref="IMetadataProvider.PluginId"/> when a pre-built DLL uses a different internal name.
    /// Always prefer this over <see cref="GetMetadataProviders"/> when you need the plugin ID as a
    /// database key (enrichment rows, metadata_json keys, etc.).
    /// </summary>
    IReadOnlyList<(string PluginId, IMetadataProvider Provider, string? IconUrl)> GetMetadataProviderEntries();

    /// <summary>
    /// Returns the <see cref="IMetadataProvider"/> whose <b>manifest</b> plugin ID matches, or <c>null</c>.
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

    /// <summary>Returns all loaded <see cref="IReportPlugin"/> instances across all plugins.</summary>
    IReadOnlyList<IReportPlugin> GetReportPlugins();

    /// <summary>Returns all loaded <see cref="IFileScannerPlugin"/> instances across all plugins.</summary>
    IReadOnlyList<IFileScannerPlugin> GetFileScannerPlugins();

    /// <summary>Returns all loaded <see cref="IThemePlugin"/> instances across all plugins.</summary>
    IReadOnlyList<IThemePlugin> GetThemePlugins();

    /// <summary>Returns all loaded <see cref="ISidecarFormatPlugin"/> instances across all plugins.</summary>
    IReadOnlyList<ISidecarFormatPlugin> GetSidecarFormatPlugins();

    /// <summary>
    /// Returns the <see cref="ISidecarFormatPlugin"/> whose <b>manifest</b> plugin ID matches, or <c>null</c>.
    /// </summary>
    ISidecarFormatPlugin? GetSidecarFormatPlugin(string pluginId);

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
