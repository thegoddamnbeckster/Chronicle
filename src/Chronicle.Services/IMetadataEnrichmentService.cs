using Chronicle.Core.Models;

namespace Chronicle.Services;

public enum ResetScope { Single, AllExhausted, AllForPlugin }

public enum EnrichmentMode
{
    /// <summary>Skip items already Completed — background task behaviour.</summary>
    FillGaps,
    /// <summary>Always re-fetch — user-triggered refresh behaviour.</summary>
    Force
}

public record EnrichmentOptions(
    EnrichmentMode Mode,
    /// <summary>Fix Match: user-supplied external ID. Bypasses scoring entirely.</summary>
    string?        IdOverride = null,
    /// <summary>When true, recurse into direct children after enriching self.</summary>
    bool           Cascade    = true
);

public interface IMetadataEnrichmentService
{
    // ── Main entry points — all callers use one of these ─────────────────────

    /// <summary>Enrich one item for one plugin, then optionally cascade to children.</summary>
    Task EnrichItemAsync(int mediaItemId, string pluginId,
                         EnrichmentOptions options, CancellationToken ct = default);

    /// <summary>Enrich one item across ALL applicable plugins (e.g. "Refresh All").</summary>
    Task EnrichItemAsync(int mediaItemId,
                         EnrichmentOptions options, CancellationToken ct = default);

    /// <summary>Returns the enrichment row per plugin for a given item.</summary>
    Task<IReadOnlyList<EnrichmentRecord>> GetEnrichmentRecordsAsync(
        int mediaItemId, CancellationToken ct = default);

    // ── Background / batch operations ─────────────────────────────────────────

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

public record EnrichmentRecord(
    string           PluginId,
    string?          ExternalId,
    EnrichmentStatus Status,
    DateTime?        LastCompletedAt,
    string?          ErrorMessage,
    string?          DiagnosticsJson
);

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
