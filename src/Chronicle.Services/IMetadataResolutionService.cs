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
}
