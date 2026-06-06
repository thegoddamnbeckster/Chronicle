using Chronicle.Core.Models;

namespace Chronicle.Services
{
    public record AddToLibraryRequest(int MediaItemId, LibraryStatus Status = LibraryStatus.PlanToWatch);
    public record UpdateLibraryRequest(LibraryStatus? Status, int? UserRating, string? Notes);

    public interface ILibraryService
    {
        Task<UserLibrary> AddAsync(int userId, AddToLibraryRequest request, CancellationToken ct = default);
        Task<IEnumerable<UserLibrary>> GetForUserAsync(int userId, LibraryStatus? status = null, int page = 1, int perPage = 20, bool rootOnly = false, CancellationToken ct = default);
        Task<UserLibrary?> GetEntryAsync(int userId, int mediaItemId);
        Task<UserLibrary> UpdateAsync(int userId, int entryId, UpdateLibraryRequest request);
        Task RemoveAsync(int userId, int entryId);
        /// <summary>
        /// Removes all library entries for the specified user and deletes any MediaItems
        /// that are exclusive to that user (not referenced by any other user's library).
        /// Assumes hierarchical items (seasons, episodes) are not independently tracked
        /// in other users' libraries; mid-level items shared by other users are not protected.
        /// </summary>
        Task<int> ClearAllAsync(int userId, CancellationToken ct = default);

        /// <summary>
        /// Deletes ALL media items, library entries, and interaction events.
        /// Requires <paramref name="confirmationToken"/> == "RESET".
        /// Returns the count of deleted library entries.
        /// </summary>
        Task<int> NuclearResetAsync(string confirmationToken, CancellationToken ct = default);

        /// <summary>
        /// Deletes all MediaItems created by the file scanner (identified by
        /// "fileScanner" key in metadata_json) and their associated library entries.
        /// </summary>
        Task<int> ClearScannerDataAsync(CancellationToken ct = default);
    }
}
