using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Chronicle.Services.Plugins;

public class PluginService : IPluginService
{
    private readonly ChronicleDbContext _db;
    private readonly IPluginRegistry _registry;
    private readonly ILogger _log = Log.ForContext<PluginService>();

    public PluginService(ChronicleDbContext db, IPluginRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    public async Task<List<Plugin>> GetAllPluginsAsync() =>
        await _db.Plugins.OrderBy(p => p.Name).ToListAsync();

    public async Task<Plugin?> GetPluginAsync(int id) =>
        await _db.Plugins.FindAsync(id);

    public async Task<Plugin> InstallPluginAsync(string dllPath, CancellationToken ct = default)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"Plugin DLL not found: {dllPath}", dllPath);

        // Load the plugin first to read its manifest
        var tempSettings = new Dictionary<string, string>();
        var loaded = await _registry.LoadPluginAsync(0, dllPath, tempSettings, ct);
        var manifest = loaded.Manifest;

        // Check for duplicate
        var existing = await _db.Plugins.FirstOrDefaultAsync(p => p.PluginId == manifest.PluginId, ct);
        if (existing != null)
            throw new InvalidOperationException(
                $"Plugin '{manifest.PluginId}' is already installed (db id {existing.Id}).");

        var plugin = new Plugin
        {
            PluginId = manifest.PluginId,
            Name = manifest.Name,
            Version = manifest.Version,
            Author = manifest.Author,
            Description = manifest.Description,
            DllPath = dllPath,
            IsEnabled = true,
            InstalledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.Plugins.Add(plugin);
        await _db.SaveChangesAsync(ct);

        // Reload with the real db id
        _registry.UnloadPlugin(0);
        await _registry.LoadPluginAsync(plugin.Id, dllPath, tempSettings, ct);

        _log.Information("Installed plugin {PluginId} (db id {Id})", plugin.PluginId, plugin.Id);
        return plugin;
    }

    public async Task UpdateSettingsAsync(int id, Dictionary<string, string> settings)
    {
        var plugin = await _db.Plugins.FindAsync(id)
            ?? throw new InvalidOperationException($"Plugin with id {id} not found.");

        plugin.SettingsJson = JsonSerializer.Serialize(settings);
        plugin.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Reconfigure live providers (metadata + import) if the plugin is loaded
        if (plugin.IsEnabled)
        {
            var metaProvider = _registry.GetMetadataProvider(plugin.PluginId);
            if (metaProvider != null)
            {
                metaProvider.Configure(settings);
                _log.Information("Reconfigured live metadata provider {PluginId}", plugin.PluginId);
            }

            var importProvider = _registry.GetImportProvider(plugin.PluginId);
            if (importProvider != null)
            {
                importProvider.Configure(settings);
                _log.Information("Reconfigured live import provider {PluginId}", plugin.PluginId);
            }
        }
    }

    public async Task EnablePluginAsync(int id)
    {
        var plugin = await _db.Plugins.FindAsync(id)
            ?? throw new InvalidOperationException($"Plugin with id {id} not found.");

        plugin.IsEnabled = true;
        plugin.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var settings = DeserializeSettings(plugin.SettingsJson);
        await _registry.LoadPluginAsync(plugin.Id, plugin.DllPath, settings);

        _log.Information("Enabled plugin {PluginId}", plugin.PluginId);
    }

    public async Task DisablePluginAsync(int id)
    {
        var plugin = await _db.Plugins.FindAsync(id)
            ?? throw new InvalidOperationException($"Plugin with id {id} not found.");

        plugin.IsEnabled = false;
        plugin.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _registry.UnloadPlugin(id);
        _log.Information("Disabled plugin {PluginId}", plugin.PluginId);
    }

    public async Task UninstallPluginAsync(int id)
    {
        var plugin = await _db.Plugins.FindAsync(id)
            ?? throw new InvalidOperationException($"Plugin with id {id} not found.");

        _registry.UnloadPlugin(id);
        _db.Plugins.Remove(plugin);
        await _db.SaveChangesAsync();

        _log.Information("Uninstalled plugin {PluginId} (db id {Id})", plugin.PluginId, id);
    }

    public async Task<bool?> HealthCheckAsync(int id, CancellationToken ct = default)
    {
        var plugin = await _db.Plugins.FindAsync(new object[] { id }, ct);
        if (plugin is null) return null;

        var provider = _registry.GetMetadataProvider(plugin.PluginId);
        if (provider is null) return null;

        return await provider.HealthCheckAsync(ct);
    }

    private static IReadOnlyDictionary<string, string> DeserializeSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? new Dictionary<string, string>();
    }
}
