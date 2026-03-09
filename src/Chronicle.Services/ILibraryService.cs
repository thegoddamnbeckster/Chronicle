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
        Task<int> ClearAllAsync(int userId, CancellationToken ct = default);
    }
}
