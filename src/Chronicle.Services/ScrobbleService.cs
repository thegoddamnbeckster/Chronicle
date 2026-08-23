using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Matching;
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
            var mediaItemId = request.MediaItemId
                ?? (await FindOrCreateMediaItemAsync(request, ct)).Id;

            var mediaItemExists = await _context.MediaItems.AnyAsync(m => m.Id == mediaItemId, ct);
            if (!mediaItemExists)
                throw new MediaNotFoundException(mediaItemId);

            var markedAsWatched = request.ProgressPercent >= WatchedThreshold;
            var timestamp = request.Timestamp ?? DateTime.UtcNow;

            var evt = new InteractionEvent
            {
                UserId          = userId,
                MediaItemId     = mediaItemId,
                Timestamp       = timestamp,
                ProgressPercent = request.ProgressPercent,
                DeviceName      = request.DeviceName,
                MarkedAsWatched = markedAsWatched,
                CreatedAt       = DateTime.UtcNow
            };

            _context.InteractionEvents.Add(evt);

            if (markedAsWatched)
                await UpdateLibraryStatusAsync(userId, mediaItemId, ct);

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
                                  && e.MediaItemId == mediaItemId
                                  && e.Timestamp == timestamp, ct);
                return new ScrobbleResult(existing, markedAsWatched);
            }

            return new ScrobbleResult(evt, markedAsWatched);
        }

        /// <summary>
        /// Resolves a <see cref="ScrobbleRequest"/> that arrived without a Chronicle
        /// MediaItemId (e.g. from the Kodi addon scrobbling an item Chronicle has never
        /// seen before) — matches by external ID first, then title+year scoped to media
        /// type, then creates a stub item. The type resolution and title/year matching
        /// themselves live in <see cref="MediaItemMatcher"/>, shared with
        /// <c>ImportService.FindOrCreateMediaItemAsync</c> and
        /// <c>SyncOrchestrationService.MatchOrCreateAsync</c> — those three used to each
        /// carry their own copy, which had drifted out of sync (see MediaItemMatcher's own
        /// docs for the bug that caused).
        /// </summary>
        private async Task<MediaItem> FindOrCreateMediaItemAsync(ScrobbleRequest request, CancellationToken ct)
        {
            var externalIds = request.ExternalIds ?? new Dictionary<string, string>();

            foreach (var (source, extId) in externalIds)
            {
                var match = await _context.MediaExternalIds
                    .Include(x => x.MediaItem)
                    .FirstOrDefaultAsync(x => x.Source == source.ToLowerInvariant() && x.ExternalId == extId, ct);
                if (match?.MediaItem != null)
                    return match.MediaItem;
            }

            if (string.IsNullOrWhiteSpace(request.Title))
                throw new ArgumentException(
                    "Scrobble request has no MediaItemId and no Title to create a stub item from.");

            // Only attempts a title+year match when a media type can be confidently
            // resolved -- an omitted/unrecognized MediaType skips straight to stub creation
            // rather than matching unscoped (which would risk the exact cross-type
            // collision this scoping exists to prevent; see MediaItemMatcher docs).
            var matchTypeId = await MediaItemMatcher.TryResolveMediaTypeIdForMatchAsync(_context, request.MediaType, ct);
            if (matchTypeId.HasValue)
            {
                var titleMatch = await MediaItemMatcher.FindByTitleYearAsync(
                    _context, request.Title, request.Year, matchTypeId.Value, ct);

                if (titleMatch != null)
                {
                    await StoreExternalIdsAsync(titleMatch.Id, externalIds, ct);
                    return titleMatch;
                }
            }

            var stubTypeId = await MediaItemMatcher.ResolveMediaTypeIdForStubAsync(_context, request.MediaType, ct);
            var stub = new MediaItem
            {
                MediaTypeId    = stubTypeId,
                Name           = request.Title,
                Year           = request.Year,
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };
            _context.MediaItems.Add(stub);
            await _context.SaveChangesAsync(ct);   // need the id before adding external IDs

            await StoreExternalIdsAsync(stub.Id, externalIds, ct);
            await _context.SaveChangesAsync(ct);

            return stub;
        }

        private async Task StoreExternalIdsAsync(
            int mediaItemId, IReadOnlyDictionary<string, string> ids, CancellationToken ct)
        {
            foreach (var (source, extId) in ids)
            {
                var normalizedSource = source.ToLowerInvariant();
                var exists = await _context.MediaExternalIds.AnyAsync(
                    x => x.MediaItemId == mediaItemId && x.Source == normalizedSource, ct);
                if (!exists)
                    _context.MediaExternalIds.Add(new MediaExternalId
                    {
                        MediaItemId = mediaItemId,
                        Source      = normalizedSource,
                        ExternalId  = extId,
                    });
            }
        }

        public async Task<IEnumerable<InteractionEvent>> GetHistoryAsync(
            int userId, int page = 1, int perPage = 20, int? mediaItemId = null)
        {
            if (page < 1) page = 1;
            if (perPage < 1) perPage = 20;

            var query = _context.InteractionEvents
                .Include(e => e.MediaItem)
                    .ThenInclude(m => m!.MediaType)
                .Where(e => e.UserId == userId);

            if (mediaItemId.HasValue)
                query = query.Where(e => e.MediaItemId == mediaItemId.Value);

            return await query
                .OrderByDescending(e => e.Timestamp)
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToListAsync();
        }

        public async Task<(DateTime? LastWatchedAt, int WatchedCount)> GetWatchSummaryAsync(
            int userId, int mediaItemId, CancellationToken ct = default)
        {
            var watched = _context.InteractionEvents
                .Where(e => e.UserId == userId && e.MediaItemId == mediaItemId && e.MarkedAsWatched);

            var count = await watched.CountAsync(ct);
            if (count == 0)
                return (null, 0);

            var lastWatchedAt = await watched.MaxAsync(e => e.Timestamp, ct);
            return (lastWatchedAt, count);
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
