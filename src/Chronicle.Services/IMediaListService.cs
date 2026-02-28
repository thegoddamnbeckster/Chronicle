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
    Task<MediaList> CreateAsync(int userId, CreateListRequest request);

    Task<IEnumerable<MediaList>> GetAllForUserAsync(int userId);

    /// <summary>Returns the list with its items, or null if not found / not owned by userId.</summary>
    Task<MediaList?> GetByIdAsync(int userId, int listId);

    Task<MediaList> UpdateAsync(int userId, int listId, UpdateListRequest request);

    Task DeleteAsync(int userId, int listId);

    Task<MediaListItem> AddItemAsync(int userId, int listId, AddItemToListRequest request);

    Task RemoveItemAsync(int userId, int listId, int itemId);

    /// <summary>Bulk-update positions for all items in a list.</summary>
    Task ReorderAsync(int userId, int listId, IEnumerable<ReorderItem> items);
}
