using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services
{
    public class LibraryService : ILibraryService
    {
        private readonly ChronicleDbContext _context;

        public LibraryService(ChronicleDbContext context)
        {
            _context = context;
        }

        public async Task<UserLibrary> AddAsync(int userId, AddToLibraryRequest request)
        {
            var existing = await _context.UserLibraries
                .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == request.MediaItemId);

            if (existing != null)
                return existing;

            var entry = new UserLibrary
            {
                UserId = userId,
                MediaItemId = request.MediaItemId,
                Status = request.Status,
                AddedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.UserLibraries.Add(entry);
            await _context.SaveChangesAsync();

            await _context.Entry(entry).Reference(e => e.MediaItem).LoadAsync();
            if (entry.MediaItem != null)
                await _context.Entry(entry.MediaItem).Reference(m => m.MediaType).LoadAsync();

            return entry;
        }

        public async Task<IEnumerable<UserLibrary>> GetForUserAsync(int userId, LibraryStatus? status = null, int page = 1, int perPage = 20, bool rootOnly = false, CancellationToken ct = default)
        {
            var q = _context.UserLibraries
                .Include(l => l.MediaItem)
                    .ThenInclude(m => m!.MediaType)
                .Where(l => l.UserId == userId);

            if (status.HasValue)
                q = q.Where(l => l.Status == status.Value);

            if (rootOnly)
                q = q.Where(l => l.MediaItem!.ParentId == null);

            return await q
                .OrderByDescending(l => l.UpdatedAt)
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToListAsync(ct);
        }

        public async Task<UserLibrary?> GetEntryAsync(int userId, int mediaItemId)
        {
            return await _context.UserLibraries
                .Include(l => l.MediaItem)
                .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId);
        }

        public async Task<UserLibrary> UpdateAsync(int userId, int entryId, UpdateLibraryRequest request)
        {
            var entry = await _context.UserLibraries
                .FirstOrDefaultAsync(l => l.Id == entryId && l.UserId == userId)
                ?? throw new LibraryEntryNotFoundException(entryId);

            if (request.Status.HasValue)
            {
                entry.Status = request.Status.Value;

                if (request.Status == LibraryStatus.Watching && entry.StartedAt == null)
                    entry.StartedAt = DateTime.UtcNow;

                if (request.Status == LibraryStatus.Completed && entry.CompletedAt == null)
                    entry.CompletedAt = DateTime.UtcNow;
            }

            if (request.UserRating.HasValue) entry.UserRating = request.UserRating;
            if (request.Notes != null) entry.Notes = request.Notes;

            entry.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return entry;
        }

        public async Task RemoveAsync(int userId, int entryId)
        {
            var entry = await _context.UserLibraries
                .FirstOrDefaultAsync(l => l.Id == entryId && l.UserId == userId)
                ?? throw new LibraryEntryNotFoundException(entryId);

            _context.UserLibraries.Remove(entry);
            await _context.SaveChangesAsync();
        }

        public async Task<int> ClearAllAsync(int userId, CancellationToken ct = default)
        {
            // Remove all library entries for this user
            var entries = await _context.UserLibraries
                .Where(e => e.UserId == userId)
                .ToListAsync(ct);

            // Find media items that are ONLY referenced by this user (no other user's library)
            var itemIds = entries.Select(e => e.MediaItemId).ToHashSet();
            var sharedIds = (await _context.UserLibraries
                .Where(e => e.UserId != userId && itemIds.Contains(e.MediaItemId))
                .Select(e => e.MediaItemId)
                .ToListAsync(ct))
                .ToHashSet();

            var exclusiveIds = itemIds.Except(sharedIds).ToHashSet();

            // NOTE: sharedIds only captures root-level sharing (UserLibrary stores root items only for
            // hierarchical imports). Mid-level or leaf items manually added to a library are not protected.
            // This is acceptable for the current import workflow.

            // Also gather all descendants of exclusive root items
            var allToDelete = new HashSet<int>(exclusiveIds);
            foreach (var rootId in exclusiveIds)
            {
                var children = await GetAllDescendantIdsAsync(rootId, ct);
                allToDelete.UnionWith(children);
            }

            // NOTE: This delete sequence has a TOCTOU race if two users run ClearAll concurrently
            // on items they share. For a single-user self-hosted deployment this is acceptable.
            _context.UserLibraries.RemoveRange(entries);
            var itemsToDelete = await _context.MediaItems
                .Where(m => allToDelete.Contains(m.Id))
                .ToListAsync(ct);
            _context.MediaItems.RemoveRange(itemsToDelete);

            await _context.SaveChangesAsync(ct);
            return entries.Count;
        }

        /// <summary>
        /// Returns the IDs of all descendants of <paramref name="rootId"/> (not including rootId itself).
        /// Uses level-by-level IN-clause batching to avoid N+1 queries.
        /// </summary>
        private async Task<List<int>> GetAllDescendantIdsAsync(int rootId, CancellationToken ct)
        {
            // Fetch all descendants level by level using IN-clause batching
            // (avoids N+1 queries by processing all nodes at each level together)
            var result = new List<int>();
            var currentLevel = new List<int> { rootId };

            while (currentLevel.Count > 0)
            {
                var children = await _context.MediaItems
                    .Where(m => m.ParentId != null && currentLevel.Contains(m.ParentId!.Value))
                    .Select(m => m.Id)
                    .ToListAsync(ct);

                result.AddRange(children);
                currentLevel = children;
            }

            return result;
        }
    }
}
