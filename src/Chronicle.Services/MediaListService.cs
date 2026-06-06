using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

public class MediaListService : IMediaListService
{
    private readonly ChronicleDbContext _context;

    public MediaListService(ChronicleDbContext context)
    {
        _context = context;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<MediaList> CreateAsync(int userId, CreateListRequest request, CancellationToken ct = default)
    {
        var list = new MediaList
        {
            UserId      = userId,
            Name        = request.Name.Trim(),
            Description = request.Description?.Trim(),
            IsOrdered   = request.IsOrdered,
            CreatedAt   = DateTime.UtcNow,
            UpdatedAt   = DateTime.UtcNow,
        };

        _context.MediaLists.Add(list);
        await _context.SaveChangesAsync(ct);
        return list;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<IEnumerable<MediaList>> GetAllForUserAsync(int userId, CancellationToken ct = default)
    {
        return await _context.MediaLists
            .Where(l => l.UserId == userId)
            .Include(l => l.Items)
            .OrderByDescending(l => l.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<MediaList?> GetByIdAsync(int userId, int listId, CancellationToken ct = default)
    {
        return await _context.MediaLists
            .Include(l => l.Items.OrderBy(i => i.Position))
                .ThenInclude(i => i.MediaItem)
                    .ThenInclude(m => m!.MediaType)
            .Include(l => l.Items)
                .ThenInclude(i => i.MediaItem)
                    .ThenInclude(m => m!.ExternalIds)
            .FirstOrDefaultAsync(l => l.Id == listId && l.UserId == userId, ct);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<MediaList> UpdateAsync(int userId, int listId, UpdateListRequest request, CancellationToken ct = default)
    {
        var list = await FindOwnedAsync(userId, listId, ct);

        if (request.Name is not null)        list.Name        = request.Name.Trim();
        if (request.Description is not null) list.Description = request.Description.Trim();
        if (request.IsOrdered.HasValue)      list.IsOrdered   = request.IsOrdered.Value;

        list.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        return list;
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteAsync(int userId, int listId, CancellationToken ct = default)
    {
        var list = await FindOwnedAsync(userId, listId, ct);
        _context.MediaLists.Remove(list);
        await _context.SaveChangesAsync(ct);
    }

    // ── Items ─────────────────────────────────────────────────────────────────

    public async Task<MediaListItem> AddItemAsync(int userId, int listId, AddItemToListRequest request, CancellationToken ct = default)
    {
        // Ensure the list belongs to this user
        var list = await FindOwnedAsync(userId, listId, ct);

        // Guard: duplicate
        bool exists = await _context.MediaListItems
            .AnyAsync(i => i.ListId == listId && i.MediaItemId == request.MediaItemId, ct);
        if (exists)
            throw new DuplicateListItemException(request.MediaItemId);

        // If no position supplied for an ordered list, append at the end
        int position = request.Position;
        if (list.IsOrdered && position == 0)
        {
            var maxPos = await _context.MediaListItems
                .Where(i => i.ListId == listId)
                .Select(i => (int?)i.Position)
                .MaxAsync(ct);
            position = (maxPos ?? -1) + 1;
        }

        var item = new MediaListItem
        {
            ListId      = listId,
            MediaItemId = request.MediaItemId,
            Position    = position,
            Notes       = request.Notes?.Trim(),
            AddedAt     = DateTime.UtcNow,
        };

        _context.MediaListItems.Add(item);

        list.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Race condition: another request inserted the same (list, media) pair concurrently.
            _context.ChangeTracker.Clear();
            throw new DuplicateListItemException(request.MediaItemId);
        }

        // Return with navigation loaded
        await _context.Entry(item)
            .Reference(i => i.MediaItem)
            .LoadAsync(ct);

        return item;
    }

    public async Task RemoveItemAsync(int userId, int listId, int itemId, CancellationToken ct = default)
    {
        var list = await FindOwnedAsync(userId, listId, ct);

        var item = await _context.MediaListItems
            .FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, ct)
            ?? throw new MediaListItemNotFoundException(itemId);

        _context.MediaListItems.Remove(item);
        list.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    public async Task ReorderAsync(int userId, int listId, IEnumerable<ReorderItem> items, CancellationToken ct = default)
    {
        var list = await FindOwnedAsync(userId, listId, ct);

        var dbItems = await _context.MediaListItems
            .Where(i => i.ListId == listId)
            .ToListAsync(ct);

        foreach (var reorder in items)
        {
            var dbItem = dbItems.FirstOrDefault(i => i.Id == reorder.ItemId);
            if (dbItem is not null)
                dbItem.Position = reorder.Position;
        }

        list.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<MediaList> FindOwnedAsync(int userId, int listId, CancellationToken ct)
    {
        return await _context.MediaLists
            .FirstOrDefaultAsync(l => l.Id == listId && l.UserId == userId, ct)
            ?? throw new MediaListNotFoundException(listId);
    }
}
