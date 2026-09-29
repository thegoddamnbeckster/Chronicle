using Chronicle.Services.Plugins;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Routes background task execution for installed plugins.
/// Well-known task IDs delegate to Chronicle's internal services; any other task ID is
/// dispatched to the matching <see cref="Chronicle.Plugins.IPluginTask"/> discovered in the
/// plugin's own assembly by <see cref="IPluginRegistry"/>.
/// </summary>
public sealed class PluginTaskRunner : IPluginTaskRunner
{
    private const string FetchMissing = "fetch-missing-metadata";
    private const string ResyncAll    = "resync-all-metadata";
    private const string ImportAll    = "import-all";
    private const string DeltaSync    = "delta-sync";

    /// <summary>
    /// Task IDs this class handles internally without needing an <see cref="Chronicle.Plugins.IPluginTask"/>
    /// implementation. Exposed so callers that need to judge whether a plugin task row is
    /// actually runnable (e.g. <c>BackgroundTasksController</c>) can check well-known IDs and
    /// custom <see cref="IPluginRegistry.GetPluginTask"/> lookups with the same authority this
    /// runner itself uses, instead of assuming every row for an installed plugin is runnable.
    /// </summary>
    public static readonly IReadOnlySet<string> WellKnownTaskIds =
        new HashSet<string> { FetchMissing, ResyncAll, ImportAll, DeltaSync };

    private readonly IMetadataEnrichmentService _enrichment;
    private readonly ISyncOrchestrationService _sync;
    private readonly IPluginRegistry _registry;
    private readonly ILogger _log = Log.ForContext<PluginTaskRunner>();

    public PluginTaskRunner(
        IMetadataEnrichmentService enrichment, ISyncOrchestrationService sync, IPluginRegistry registry)
    {
        _enrichment = enrichment;
        _sync       = sync;
        _registry   = registry;
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
                await _enrichment.ResyncAllForPluginAsync(pluginId, ct);
                return;

            case ImportAll:
                _log.Information("PluginTaskRunner: running import-all for plugin {PluginId}", pluginId);
                await _sync.SyncAsync(pluginId, fullSync: true, ct: ct);
                return;

            case DeltaSync:
                _log.Information("PluginTaskRunner: running delta-sync for plugin {PluginId}", pluginId);
                await _sync.SyncAsync(pluginId, fullSync: false, ct: ct);
                return;

            default:
                var customTask = _registry.GetPluginTask(pluginId, taskId);
                if (customTask is not null)
                {
                    _log.Information(
                        "PluginTaskRunner: running custom task '{TaskId}' for plugin {PluginId}", taskId, pluginId);
                    await customTask.RunAsync(ct);
                    return;
                }

                _log.Warning(
                    "PluginTaskRunner: no handler for task_id '{TaskId}' on plugin '{PluginId}' — no well-known " +
                    "handler and no matching IPluginTask was discovered in the plugin's loaded assembly.",
                    taskId, pluginId);
                break;
        }
    }
}
