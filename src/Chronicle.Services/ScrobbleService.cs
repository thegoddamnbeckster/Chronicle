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

        public async Task<ScrobbleResult> ScrobbleAsync(int userId, ScrobbleRequest request, CancellationToken ct = default)
        {
            var mediaItem = await _context.MediaItems.FindAsync([request.MediaItemId], ct)
                ?? throw new MediaNotFoundException(request.MediaItemId);

            var markedAsWatched = request.ProgressPercent >= WatchedThreshold;
            var timestamp = request.Timestamp ?? DateTime.UtcNow;

            var evt = new InteractionEvent
            {
                UserId          = userId,
                MediaItemId     = request.MediaItemId,
                Timestamp       = timestamp,
                ProgressPercent = request.ProgressPercent,
                DeviceName      = request.DeviceName,
                MarkedAsWatched = markedAsWatched,
                CreatedAt       = DateTime.UtcNow
            };

            _context.InteractionEvents.Add(evt);

            if (markedAsWatched)
                await UpdateLibraryStatusAsync(userId, request.MediaItemId, ct);

            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Duplicate scrobble — same (user, item, timestamp) already recorded.
                // Clear the failed insert and return the pre-existing event.
                _context.ChangeTracker.Clear();
                var existing = await _context.InteractionEvents
                    .FirstAsync(e => e.UserId == userId
                                  && e.MediaItemId == request.MediaItemId
                                  && e.Timestamp == timestamp, ct);
                return new ScrobbleResult(existing, markedAsWatched);
            }

            return new ScrobbleResult(evt, markedAsWatched);
        }

        public async Task<IEnumerable<InteractionEvent>> GetHistoryAsync(int userId, int page = 1, int perPage = 20)
        {
            if (page < 1) page = 1;
            if (perPage < 1) perPage = 20;

            return await _context.InteractionEvents
                .Include(e => e.MediaItem)
                    .ThenInclude(m => m!.MediaType)
                .Where(e => e.UserId == userId)
                .OrderByDescending(e => e.Timestamp)
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToListAsync();
        }

        private async Task UpdateLibraryStatusAsync(int userId, int mediaItemId, CancellationToken ct)
        {
            var entry = await _context.UserLibraries
                .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId, ct);

            if (entry == null)
            {
                entry = new UserLibrary
                {
                    UserId      = userId,
                    MediaItemId = mediaItemId,
                    Status      = LibraryStatus.Watching,
                    AddedAt     = DateTime.UtcNow,
                    UpdatedAt   = DateTime.UtcNow,
                    StartedAt   = DateTime.UtcNow
                };
                _context.UserLibraries.Add(entry);
            }
            else if (entry.Status == LibraryStatus.PlanToWatch)
            {
                entry.Status    = LibraryStatus.Watching;
                entry.StartedAt ??= DateTime.UtcNow;
                entry.UpdatedAt = DateTime.UtcNow;
            }
        }

        private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
            ex.InnerException?.Message.Contains("UNIQUE constraint failed",
                StringComparison.OrdinalIgnoreCase) == true;
    }
}
