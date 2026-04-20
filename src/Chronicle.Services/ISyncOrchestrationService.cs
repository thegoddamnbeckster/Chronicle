namespace Chronicle.Services;

public record SyncSummary(
    int  ItemsMatched,
    int  StubsCreated,
    int  WatchEventsAdded,
    int  CreditsAdded,
    IReadOnlyList<string> Errors
);

public interface ISyncOrchestrationService
{
    /// <summary>
    /// Syncs all available data from the specified import provider.
    /// </summary>
    /// <param name="pluginId">The plugin ID declared in the provider's manifest (e.g. "chronicle.plugin.trakt").</param>
    /// <param name="fullSync">When true, ignore last_synced_at and pull all history.</param>
    Task<SyncSummary> SyncAsync(string pluginId, bool fullSync = false, CancellationToken ct = default);
}
