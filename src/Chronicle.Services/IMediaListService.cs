using Chronicle.Core.Models;

namespace Chronicle.Services;

public record CreateListRequest(
    string Name,
    string? Description,
    bool IsOrdered = true
);

public record UpdateListRequest(
    string? Name,
    string? Description,
    bool? IsOrdered
);

public record AddItemToListRequest(
    int MediaItemId,
    int Position = 0,
    string? Notes = null
);

public record ReorderItem(int ItemId, int Position);

public interface IMediaListService
{
    Task<MediaList> CreateAsync(int userId, CreateListRequest request, CancellationToken ct = default);

    Task<IEnumerable<MediaList>> GetAllForUserAsync(int userId, CancellationToken ct = default);

    /// <summary>Returns the list with its items, or null if not found / not owned by userId.</summary>
    Task<MediaList?> GetByIdAsync(int userId, int listId, CancellationToken ct = default);

    Task<MediaList> UpdateAsync(int userId, int listId, UpdateListRequest request, CancellationToken ct = default);

    Task DeleteAsync(int userId, int listId, CancellationToken ct = default);

    Task<MediaListItem> AddItemAsync(int userId, int listId, AddItemToListRequest request, CancellationToken ct = default);

    Task RemoveItemAsync(int userId, int listId, int itemId, CancellationToken ct = default);

    /// <summary>Bulk-update positions for all items in a list.</summary>
    Task ReorderAsync(int userId, int listId, IEnumerable<ReorderItem> items, CancellationToken ct = default);
}
