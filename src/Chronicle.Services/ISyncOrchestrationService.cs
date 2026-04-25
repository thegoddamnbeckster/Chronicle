namespace Chronicle.Services;

public record SyncSummary(
    int  ItemsMatched,
    int  StubsCreated,
    int  WatchEventsAdded,
    int  CreditsAdded,
    IReadOnlyList<string> Errors
);

/// <summary>Snapshot of a background sync job.</summary>
public record SyncJobSnapshot(
    string       Status,          // "running" | "complete" | "failed"
    SyncSummary? Summary,
    string?      Error
);

public interface ISyncOrchestrationService
{
    /// <summary>
    /// Syncs all available data from the specified import provider.
    /// </summary>
    /// <param name="pluginId">The plugin ID declared in the provider's manifest (e.g. "chronicle.plugin.trakt").</param>
    /// <param name="fullSync">When true, ignore last_synced_at and pull all history.</param>
    /// <param name="userId">
    /// The user whose library entries should be created/updated.
    /// When null (e.g. background task), the first registered user in the DB is used as a fallback.
    /// </param>
    Task<SyncSummary> SyncAsync(string pluginId, bool fullSync = false, int? userId = null, CancellationToken ct = default);
}

/// <summary>
/// Singleton tracker for fire-and-forget sync jobs initiated via the HTTP endpoint.
/// Follows the same pattern as ScanProgressService / ImportProgressService.
/// </summary>
public interface ISyncJobTracker
{
    /// <summary>Starts a sync in the background and returns a short job ID.</summary>
    string Enqueue(Func<Task<SyncSummary>> work);

    /// <summary>Returns the current snapshot for a job, or null if the ID is unknown.</summary>
    SyncJobSnapshot? GetSnapshot(string jobId);
}
