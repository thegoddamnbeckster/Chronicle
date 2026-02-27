using Serilog;
using Serilog.Events;

namespace Chronicle.Services.Plugins;

/// <summary>
/// Creates per-plugin Serilog loggers that write to both the main Chronicle log
/// and to a dedicated plugin sub-folder under logs/plugins/{pluginId}/.
///
/// Each plugin logger:
///  - Forwards all events to the global Log.Logger (so they appear in the main log)
///  - Writes its own rolling daily file under logs/plugins/{pluginId}/
///  - Uses the same retention settings as the main log
///  - Enriches all events with a "PluginId" property for filtering
///
/// Usage (from the plugin host, Phase 2 Step 4):
///   var logger = PluginLoggerFactory.CreatePluginLogger("chronicle.plugin.tmdb", retainedLogDays: 30);
///   plugin.SetLogger(logger);
/// </summary>
public static class PluginLoggerFactory
{
    /// <summary>
    /// Creates a dedicated logger for a plugin.
    /// </summary>
    /// <param name="pluginId">
    /// The plugin's unique identifier (e.g. "chronicle.plugin.tmdb").
    /// Must be filesystem-safe — lowercase, dots and hyphens only.
    /// </param>
    /// <param name="retainedLogDays">
    /// How many daily log files to keep. Defaults to 30.
    /// </param>
    /// <returns>A Serilog ILogger scoped to the plugin.</returns>
    public static ILogger CreatePluginLogger(string pluginId, int retainedLogDays = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        // Use AppContext.BaseDirectory so plugin logs land next to the exe,
        // not relative to the working directory (which is System32 for services).
        var pluginLogPath = Path.Combine(
            AppContext.BaseDirectory, "logs", "plugins", pluginId, $"{pluginId}-.log");

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            // Forward every event to the global (main) logger so all plugin
            // activity is visible in chronicle-YYYYMMDD.log.
            .WriteTo.Logger(Log.Logger)
            // Per-plugin rolling file — its own folder, same retention policy.
            .WriteTo.File(
                path: pluginLogPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retainedLogDays,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .Enrich.FromLogContext()
            .Enrich.WithProperty("PluginId", pluginId)
            .CreateLogger()
            .ForContext("PluginId", pluginId);
    }
}
