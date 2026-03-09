using Chronicle.Core.Models;

namespace Chronicle.Services
{
    public record AddToLibraryRequest(int MediaItemId, LibraryStatus Status = LibraryStatus.PlanToWatch);
    public record UpdateLibraryRequest(LibraryStatus? Status, int? UserRating, string? Notes);

    public interface ILibraryService
    {
        Task<UserLibrary> AddAsync(int userId, AddToLibraryRequest request);
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
    }
}
