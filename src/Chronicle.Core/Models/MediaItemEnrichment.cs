namespace Chronicle.Core.Models;

/// <summary>
/// Single source of truth for one item's enrichment state from one plugin.
/// Replaces EnrichmentStatus (enrichment_statuses table), MediaExternalId
/// (media_external_ids table), and MediaItemRefreshLog (media_item_refresh_logs table).
/// </summary>
public class MediaItemEnrichment
{
    public int     Id          { get; set; }
    public int     MediaItemId { get; set; }
    public string  PluginId    { get; set; } = string.Empty;

    /// <summary>The provider's external ID for this item. Null until matched.</summary>
    public string? ExternalId  { get; set; }

    public EnrichmentStatus Status          { get; set; } = EnrichmentStatus.Pending;
    public int              RetryCount      { get; set; }
    public int              MaxRetries      { get; set; } = 3;
    public DateTime?        LastAttemptedAt { get; set; }
    public DateTime?        LastCompletedAt { get; set; }
    public string?          ErrorMessage    { get; set; }

    /// <summary>
    /// JSON blob: search candidates considered, scores, signals used, threshold at match time.
    /// </summary>
    public string? DiagnosticsJson { get; set; }

    // Navigation
    public MediaItem? MediaItem { get; set; }
}
