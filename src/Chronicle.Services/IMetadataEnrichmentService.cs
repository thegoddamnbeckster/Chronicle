namespace Chronicle.Services;

public enum ResetScope { Single, AllExhausted, AllForPlugin }

public interface IMetadataEnrichmentService
{
    /// <summary>Run enrichment for all pending/retryable items for a specific plugin.</summary>
    Task EnrichPendingAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Run enrichment for all registered plugins.</summary>
    Task EnrichAllAsync(CancellationToken ct = default);

    /// <summary>Reset enrichment status rows.</summary>
    Task ResetAsync(string pluginId, ResetScope scope, int? mediaItemId = null, CancellationToken ct = default);

    /// <summary>Mark a specific item as skipped for a plugin.</summary>
    Task SkipAsync(int mediaItemId, string pluginId, CancellationToken ct = default);

    /// <summary>Get enrichment statistics per plugin.</summary>
    Task<IReadOnlyList<EnrichmentStats>> GetStatsAsync(CancellationToken ct = default);

    /// <summary>Get paginated enrichment items for a plugin, with optional status/search filters.</summary>
    Task<PagedEnrichmentItems> GetItemsAsync(
        string pluginId,
        string? status,
        int page,
        int pageSize,
        string? search,
        CancellationToken ct);
}

public record EnrichmentStats(
    string PluginId,
    string PluginName,
    int Pending,
    int Completed,
    int Failed,
    int Exhausted,
    int NotFound,
    int Skipped
);
