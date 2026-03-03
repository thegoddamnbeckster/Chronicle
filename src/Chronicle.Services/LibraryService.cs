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

        public async Task<IEnumerable<UserLibrary>> GetForUserAsync(int userId, LibraryStatus? status = null, int page = 1, int perPage = 20)
        {
            var q = _context.UserLibraries
                .Include(l => l.MediaItem)
                    .ThenInclude(m => m!.MediaType)
                .Where(l => l.UserId == userId);

            if (status.HasValue)
                q = q.Where(l => l.Status == status.Value);

            return await q
                .OrderByDescending(l => l.UpdatedAt)
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToListAsync();
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
    }
}
