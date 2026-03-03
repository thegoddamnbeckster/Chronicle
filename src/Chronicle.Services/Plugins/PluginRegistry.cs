using System.Reflection;
using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Serilog;

namespace Chronicle.Services.Plugins;

/// <summary>
/// Thread-safe in-process plugin registry.
/// Loads plugin assemblies into isolated <see cref="PluginLoadContext"/> instances
/// and exposes discovered <see cref="IMetadataProvider"/> and <see cref="IWidgetPlugin"/>
/// instances to the rest of the application.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry, IDisposable
{
    private readonly ILogger _log = Log.ForContext<PluginRegistry>();
    private readonly Dictionary<int, LoadedPlugin> _plugins = [];
    private readonly object _lock = new();

    /// <inheritdoc/>
    public IReadOnlyList<IMetadataProvider> GetMetadataProviders()
    {
        lock (_lock)
            return _plugins.Values.SelectMany(p => p.MetadataProviders).ToList();
    }

    /// <inheritdoc/>
    public IMetadataProvider? GetMetadataProvider(string pluginId)
    {
        lock (_lock)
            return _plugins.Values
                .SelectMany(p => p.MetadataProviders)
                .FirstOrDefault(m => m.PluginId == pluginId);
    }

    /// <inheritdoc/>
    public IReadOnlyList<IWidgetPlugin> GetWidgetPlugins()
    {
        lock (_lock)
            return _plugins.Values.SelectMany(p => p.WidgetPlugins).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<IImportProvider> GetImportProviders()
    {
        lock (_lock)
            return _plugins.Values.SelectMany(p => p.ImportProviders).ToList();
    }

    /// <inheritdoc/>
    public IImportProvider? GetImportProvider(string pluginId)
    {
        lock (_lock)
            return _plugins.Values
                .SelectMany(p => p.ImportProviders)
                .FirstOrDefault(ip => ip.PluginId == pluginId);
    }

    /// <inheritdoc/>
    public IReadOnlyList<IReportPlugin> GetReportPlugins()
    {
        lock (_lock)
            return _plugins.Values.SelectMany(p => p.ReportPlugins).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<IFileScannerPlugin> GetFileScannerPlugins()
    {
        lock (_lock)
            return _plugins.Values.SelectMany(p => p.FileScannerPlugins).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<LoadedPlugin> GetLoadedPlugins()
    {
        lock (_lock)
            return _plugins.Values.ToList();
    }

    /// <inheritdoc/>
    public async Task<LoadedPlugin> LoadPluginAsync(
        int dbId,
        string dllPath,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken ct = default)
    {
        _log.Information("Loading plugin from {DllPath} (db id {DbId})", dllPath, dbId);

        // Read manifest.json from the same directory
        var manifestPath = Path.Combine(Path.GetDirectoryName(dllPath)!, "manifest.json");
        PluginManifest manifest;
        if (File.Exists(manifestPath))
        {
            var json = await File.ReadAllTextAsync(manifestPath, ct);
            manifest = JsonSerializer.Deserialize<PluginManifest>(json)
                ?? throw new InvalidOperationException($"Could not deserialise manifest at {manifestPath}");
        }
        else
        {
            // Synthesise a minimal manifest from the file name if no manifest.json exists
            var baseName = Path.GetFileNameWithoutExtension(dllPath);
            manifest = new PluginManifest
            {
                PluginId = baseName.ToLowerInvariant(),
                Name = baseName,
                Version = "0.0.0",
                Author = "Unknown"
            };
            _log.Warning("No manifest.json found alongside {DllPath} — using defaults", dllPath);
        }

        var loadContext = new PluginLoadContext(dllPath);
        var assembly = loadContext.LoadFromAssemblyPath(dllPath);

        var providers       = DiscoverAndInstantiate<IMetadataProvider>(assembly, _log);
        var widgets         = DiscoverAndInstantiate<IWidgetPlugin>(assembly, _log);
        var importProviders = DiscoverAndInstantiate<IImportProvider>(assembly, _log);
        var reportPlugins   = DiscoverAndInstantiate<IReportPlugin>(assembly, _log);
        var fileScanners    = DiscoverAndInstantiate<IFileScannerPlugin>(assembly, _log);

        // Configure all providers with the supplied settings
        foreach (var provider in providers)
        {
            try
            {
                provider.Configure(settings);
                _log.Information("Configured metadata provider {PluginId}", provider.PluginId);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to configure metadata provider {PluginId}", provider.PluginId);
            }
        }

        foreach (var ip in importProviders)
        {
            try
            {
                ip.Configure(settings);
                _log.Information("Configured import provider {PluginId}", ip.PluginId);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to configure import provider {PluginId}", ip.PluginId);
            }
        }

        foreach (var fs in fileScanners)
        {
            try
            {
                fs.Configure(settings);
                _log.Information("Configured file scanner {PluginId}", fs.PluginId);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to configure file scanner {PluginId}", fs.PluginId);
            }
        }

        var loaded = new LoadedPlugin(loadContext, dbId, manifest, providers, widgets,
            importProviders, reportPlugins, fileScanners);

        lock (_lock)
        {
            // Unload any existing version before replacing
            if (_plugins.TryGetValue(dbId, out var existing))
            {
                existing.Dispose();
                _log.Information("Replaced existing loaded plugin for db id {DbId}", dbId);
            }
            _plugins[dbId] = loaded;
        }

        _log.Information(
            "Plugin loaded: {Name} v{Version} — {Providers} metadata, {Widgets} widget(s), {Import} import, {Reports} report(s), {Scanners} scanner(s)",
            manifest.Name, manifest.Version, providers.Count, widgets.Count,
            importProviders.Count, reportPlugins.Count, fileScanners.Count);

        return loaded;
    }

    /// <inheritdoc/>
    public void UnloadPlugin(int dbId)
    {
        lock (_lock)
        {
            if (!_plugins.Remove(dbId, out var plugin))
                return;
            plugin.Dispose();
            _log.Information("Plugin unloaded (db id {DbId})", dbId);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var plugin in _plugins.Values)
                plugin.Dispose();
            _plugins.Clear();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<T> DiscoverAndInstantiate<T>(Assembly assembly, ILogger log)
        where T : class
    {
        var results = new List<T>();
        foreach (var type in assembly.GetExportedTypes())
        {
            if (!typeof(T).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                continue;

            try
            {
                var instance = (T)Activator.CreateInstance(type)!;
                results.Add(instance);
                log.Debug("Instantiated {Type} as {Interface}", type.FullName, typeof(T).Name);
            }
            catch (Exception ex)
            {
                log.Error(ex, "Failed to instantiate {Type}", type.FullName);
            }
        }
        return results;
    }
}
