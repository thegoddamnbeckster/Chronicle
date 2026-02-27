using System.Text.Json;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Chronicle.Services.Plugins;

/// <summary>
/// Background service that loads all enabled plugins from the database on application startup.
/// Plugins whose DLL is missing or fails to load are logged and skipped.
/// </summary>
public sealed class PluginHostService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPluginRegistry _registry;
    private readonly ILogger _log = Log.ForContext<PluginHostService>();

    public PluginHostService(IServiceScopeFactory scopeFactory, IPluginRegistry registry)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _log.Information("PluginHostService starting — loading enabled plugins from database");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var enabledPlugins = await db.Plugins
            .Where(p => p.IsEnabled)
            .ToListAsync(cancellationToken);

        _log.Information("Found {Count} enabled plugin(s) in database", enabledPlugins.Count);

        foreach (var plugin in enabledPlugins)
        {
            if (!File.Exists(plugin.DllPath))
            {
                _log.Warning(
                    "Plugin {PluginId} DLL not found at {DllPath} — skipping",
                    plugin.PluginId, plugin.DllPath);
                continue;
            }

            try
            {
                var settings = DeserializeSettings(plugin.SettingsJson);
                await _registry.LoadPluginAsync(plugin.Id, plugin.DllPath, settings, cancellationToken);
            }
            catch (Exception ex)
            {
                _log.Error(ex,
                    "Failed to load plugin {PluginId} from {DllPath}",
                    plugin.PluginId, plugin.DllPath);
            }
        }

        _log.Information("PluginHostService startup complete");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log.Information("PluginHostService stopping — all plugin load contexts will be unloaded");
        // PluginRegistry.Dispose() handles unloading via DI container disposal
        return Task.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, string> DeserializeSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return new Dictionary<string, string>();

        return JsonSerializer.Deserialize<Dictionary<string, string>>(settingsJson)
            ?? new Dictionary<string, string>();
    }
}
