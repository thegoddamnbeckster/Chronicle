using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Routes background task execution for installed plugins.
/// Well-known task IDs delegate to Chronicle's internal services;
/// unknown IDs are logged for future custom IPluginTask dispatch.
/// </summary>
public sealed class PluginTaskRunner : IPluginTaskRunner
{
    private const string FetchMissing = "fetch-missing-metadata";
    private const string ResyncAll    = "resync-all-metadata";

    private readonly IMetadataEnrichmentService _enrichment;
    private readonly IMetadataRefreshService    _refresh;
    private readonly ILogger _log = Log.ForContext<PluginTaskRunner>();

    public PluginTaskRunner(
        IMetadataEnrichmentService enrichment,
        IMetadataRefreshService    refresh)
    {
        _enrichment = enrichment;
        _refresh    = refresh;
    }

    public async Task RunAsync(string pluginId, string taskId, CancellationToken ct)
    {
        switch (taskId)
        {
            case FetchMissing:
                _log.Information("PluginTaskRunner: running fetch-missing-metadata for plugin {PluginId}", pluginId);
                await _enrichment.EnrichPendingAsync(pluginId, ct);
                return;

            case ResyncAll:
                _log.Information("PluginTaskRunner: running resync-all-metadata for plugin {PluginId}", pluginId);
                await _refresh.RefreshForPluginAsync(pluginId, ct);
                return;

            default:
                // Future: discover IPluginTask from loaded plugin assembly.
                // Custom task support is planned — for now log a warning.
                _log.Warning(
                    "PluginTaskRunner: no handler for task_id '{TaskId}' on plugin '{PluginId}'. " +
                    "Custom IPluginTask dispatch is not yet implemented.",
                    taskId, pluginId);
                break;
        }
    }
}
