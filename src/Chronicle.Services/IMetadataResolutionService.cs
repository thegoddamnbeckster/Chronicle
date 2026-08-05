using Chronicle.Core.Models;
using Chronicle.Data;

namespace Chronicle.Services;

public interface IMetadataResolutionService
{
    /// Recomputes metadata_json["_resolved"] for a single item and promotes first-class columns.
    /// Does NOT call SaveChangesAsync — caller is responsible.
    Task ResolveAsync(MediaItem item, ChronicleDbContext db, CancellationToken ct = default);

    /// Bulk recompute for all items of the given media type. Streams in batches of 100.
    Task ResolveAllForMediaTypeAsync(string mediaTypeName, CancellationToken ct = default);

    /// The full set of canonical resolution field names (e.g. "composer", "poster_url") —
    /// what FieldAliasCache's extra-alias config can apply to. Fixed at compile time; adding
    /// a new canonical field is a code change, unlike its alias names which are configurable.
    IReadOnlyCollection<string> GetCanonicalFields();

    /// Pins a manually-chosen value for one canonical field on one item — it wins over the
    /// plugin-priority walk in every future ResolveAsync call (Refresh, Clear Match, Merge,
    /// sync, bulk recompute all funnel through ResolveAsync, so this is the single choke point
    /// that makes the pin durable everywhere) until explicitly cleared. Re-runs ResolveAsync
    /// before returning so the item's _resolved/first-class columns reflect it immediately.
    /// Does NOT call SaveChangesAsync — caller is responsible.
    Task SetOverrideAsync(MediaItem item, ChronicleDbContext db, string field, string url,
        string? sourcePluginId, string? sourceType, int? userId, CancellationToken ct = default);

    /// Clears one field's override on one item (idempotent — a no-op if none was set) and
    /// re-runs ResolveAsync so the field reverts to the normal priority walk immediately.
    /// Does NOT call SaveChangesAsync — caller is responsible.
    Task ClearOverrideAsync(MediaItem item, ChronicleDbContext db, string field, CancellationToken ct = default);

    /// Clears every override on one item and re-runs ResolveAsync.
    /// Does NOT call SaveChangesAsync — caller is responsible.
    Task ClearItemOverridesAsync(MediaItem item, ChronicleDbContext db, CancellationToken ct = default);

    /// Clears overrides for every item of the given media type. Streams in batches of 100,
    /// saving per batch. onBatch(processedSoFar, clearedSoFar) fires after each batch commits.
    Task<int> ClearOverridesForMediaTypeAsync(string mediaTypeName, Action<int, int>? onBatch = null, CancellationToken ct = default);

    /// Clears every override across the entire library. Streams in batches of 100, saving per
    /// batch. onBatch(processedSoFar, clearedSoFar) fires after each batch commits.
    Task<int> ClearAllOverridesLibraryWideAsync(Action<int, int>? onBatch = null, CancellationToken ct = default);
}
