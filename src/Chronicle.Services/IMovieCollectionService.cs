using Chronicle.Core.Models;
using Chronicle.Data;

namespace Chronicle.Services;

public interface IMovieCollectionService
{
    /// <summary>
    /// Inspects the movie item's plugin metadata for <c>belongsToCollection</c> data
    /// from any registered plugin that stores it in the standard shape.
    /// If found, ensures a Collection parent MediaItem exists and re-parents the movie under it.
    /// No-op if the movie has no collection data or media type is not "movies".
    /// </summary>
    Task EnsureCollectionParentAsync(
        ChronicleDbContext db,
        MediaItem movieItem,
        CancellationToken ct = default);
}
