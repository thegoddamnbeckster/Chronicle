namespace Chronicle.Core.Models;

public enum EnrichmentStatus
{
    Pending,
    Completed,
    Failed,
    Exhausted,
    NotFound,
    Skipped,
    /// <summary>
    /// The plugin could not authenticate with its upstream service.
    /// This is a terminal state — the user must fix the plugin's credentials
    /// before enrichment can be retried.
    /// </summary>
    AuthFailed
}
