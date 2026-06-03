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
}
