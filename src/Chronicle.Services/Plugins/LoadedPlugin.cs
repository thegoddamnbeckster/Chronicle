using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Services.Plugins;

/// <summary>
/// In-memory representation of a successfully loaded plugin assembly.
/// Holds references to the load context and all discovered plugin instances.
/// </summary>
public sealed class LoadedPlugin : IDisposable
{
    public PluginLoadContext LoadContext { get; }

    /// <summary>Database record id (0 if not yet persisted).</summary>
    public int DbId { get; }

    public PluginManifest Manifest { get; }

    /// <summary>All <see cref="IMetadataProvider"/> instances discovered in the assembly.</summary>
    public IReadOnlyList<IMetadataProvider> MetadataProviders { get; }

    /// <summary>All <see cref="IWidgetPlugin"/> instances discovered in the assembly.</summary>
    public IReadOnlyList<IWidgetPlugin> WidgetPlugins { get; }

    /// <summary>All <see cref="IImportProvider"/> instances discovered in the assembly.</summary>
    public IReadOnlyList<IImportProvider> ImportProviders { get; }

    /// <summary>All <see cref="IReportPlugin"/> instances discovered in the assembly.</summary>
    public IReadOnlyList<IReportPlugin> ReportPlugins { get; }

    /// <summary>All <see cref="IFileScannerPlugin"/> instances discovered in the assembly.</summary>
    public IReadOnlyList<IFileScannerPlugin> FileScannerPlugins { get; }

    /// <summary>All <see cref="IThemePlugin"/> instances discovered in the assembly.</summary>
    public IReadOnlyList<IThemePlugin> ThemePlugins { get; }

    /// <summary>All <see cref="ISidecarFormatPlugin"/> instances discovered in the assembly.</summary>
    public IReadOnlyList<ISidecarFormatPlugin> SidecarFormatPlugins { get; }

    public LoadedPlugin(
        PluginLoadContext loadContext,
        int dbId,
        PluginManifest manifest,
        IReadOnlyList<IMetadataProvider> metadataProviders,
        IReadOnlyList<IWidgetPlugin> widgetPlugins,
        IReadOnlyList<IImportProvider>? importProviders = null,
        IReadOnlyList<IReportPlugin>? reportPlugins = null,
        IReadOnlyList<IFileScannerPlugin>? fileScannerPlugins = null,
        IReadOnlyList<IThemePlugin>? themePlugins = null,
        IReadOnlyList<ISidecarFormatPlugin>? sidecarFormatPlugins = null)
    {
        LoadContext = loadContext;
        DbId = dbId;
        Manifest = manifest;
        MetadataProviders = metadataProviders;
        WidgetPlugins = widgetPlugins;
        ImportProviders = importProviders ?? [];
        ReportPlugins = reportPlugins ?? [];
        FileScannerPlugins = fileScannerPlugins ?? [];
        ThemePlugins = themePlugins ?? [];
        SidecarFormatPlugins = sidecarFormatPlugins ?? [];
    }

    /// <summary>Unloads the plugin's <see cref="AssemblyLoadContext"/>.</summary>
    public void Dispose() => LoadContext.Unload();
}
