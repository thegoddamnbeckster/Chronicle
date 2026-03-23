namespace Chronicle.Core.Models;

public class MediaItemEnrichmentStatus
{
    public int Id { get; set; }
    public int MediaItemId { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public string? ExternalId { get; set; }
    public EnrichmentStatus Status { get; set; } = EnrichmentStatus.Pending;
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public DateTime? LastAttemptedAt { get; set; }
    public DateTime? LastCompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// JSON blob capturing search diagnostics from the last enrichment attempt.
    /// Null for items never attempted or enriched before this feature was added.
    /// </summary>
    public string? DiagnosticsJson { get; set; }

    public MediaItem? MediaItem { get; set; }
}
