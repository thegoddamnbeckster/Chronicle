using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services
{
    public class ScrobbleService : IScrobbleService
    {
        private const double WatchedThreshold = 80.0;
        private readonly ChronicleDbContext _context;

        public ScrobbleService(ChronicleDbContext context)
        {
            _context = context;
        }

        public async Task<ScrobbleResult> ScrobbleAsync(int userId, ScrobbleRequest request)
        {
            var mediaItem = await _context.MediaItems.FindAsync(request.MediaItemId)
                ?? throw new MediaNotFoundException(request.MediaItemId);

            var markedAsWatched = request.ProgressPercent >= WatchedThreshold;
            var timestamp = request.Timestamp ?? DateTime.UtcNow;

            var evt = new InteractionEvent
            {
                UserId = userId,
                MediaItemId = request.MediaItemId,
                Timestamp = timestamp,
                ProgressPercent = request.ProgressPercent,
                DeviceName = request.DeviceName,
                MarkedAsWatched = markedAsWatched,
                CreatedAt = DateTime.UtcNow
            };

            _context.InteractionEvents.Add(evt);

            if (markedAsWatched)
                await UpdateLibraryStatusAsync(userId, request.MediaItemId, mediaItem);

            await _context.SaveChangesAsync();

            return new ScrobbleResult(evt, markedAsWatched);
        }

        public async Task<IEnumerable<InteractionEvent>> GetHistoryAsync(int userId, int page = 1, int perPage = 20)
        {
            return await _context.InteractionEvents
                .Include(e => e.MediaItem)
                    .ThenInclude(m => m!.MediaType)
                .Where(e => e.UserId == userId)
                .OrderByDescending(e => e.Timestamp)
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToListAsync();
        }

        private async Task UpdateLibraryStatusAsync(int userId, int mediaItemId, MediaItem mediaItem)
        {
            var entry = await _context.UserLibraries
                .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId);

            if (entry == null)
            {
                entry = new UserLibrary
                {
                    UserId = userId,
                    MediaItemId = mediaItemId,
                    Status = LibraryStatus.Watching,
                    AddedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    StartedAt = DateTime.UtcNow
                };
                _context.UserLibraries.Add(entry);
            }
            else if (entry.Status == LibraryStatus.PlanToWatch)
            {
                entry.Status = LibraryStatus.Watching;
                entry.StartedAt ??= DateTime.UtcNow;
                entry.UpdatedAt = DateTime.UtcNow;
            }
        }
    }
}
