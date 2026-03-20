using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Scheduled task that enriches all pending media items with metadata
/// from installed plugins. Runs nightly at 4am (after the 3am file scan).
/// </summary>
public sealed class MetadataEnrichmentScheduledTask : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _log = Log.ForContext<MetadataEnrichmentScheduledTask>();

    public MetadataEnrichmentScheduledTask(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // ── IScheduledTask ────────────────────────────────────────────────────────

    public string TaskId      => "metadata_enrichment";
    public string DisplayName => "Metadata Enrichment";
    public string Description => "Enriches all pending media items with metadata from installed plugins.";
    public string DefaultCron => "0 4 * * *";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        _log.Information("MetadataEnrichmentScheduledTask: Starting scheduled metadata enrichment");

        using var scope = _scopeFactory.CreateScope();
        var enrichmentSvc = scope.ServiceProvider.GetRequiredService<IMetadataEnrichmentService>();

        await enrichmentSvc.EnrichAllAsync(ct);

        _log.Information("MetadataEnrichmentScheduledTask: Scheduled metadata enrichment complete");
    }
}
