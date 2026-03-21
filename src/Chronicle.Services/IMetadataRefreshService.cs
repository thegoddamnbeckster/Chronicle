using Chronicle.Core.Models;

namespace Chronicle.Services;

public interface IMetadataRefreshService
{
    /// <summary>
    /// Refreshes metadata for a single item from every active, applicable provider.
    /// Writes results to media_item_refresh_log.
    /// </summary>
    Task RefreshItemAsync(int mediaItemId, CancellationToken ct = default);

    /// <summary>
    /// Runs a full library refresh pass: all root items, all active providers.
    /// Called by the background timer and exposed for manual trigger via API.
    /// </summary>
    Task RefreshAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Re-downloads metadata for all library items using only the specified plugin.
    /// Called by the per-plugin "resync-all-metadata" scheduled task.
    /// </summary>
    Task RefreshForPluginAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Returns the most-recent refresh log entry per provider for the given item.</summary>
    Task<IReadOnlyList<MediaItemRefreshLog>> GetRefreshLogsAsync(int mediaItemId, CancellationToken ct = default);
}
