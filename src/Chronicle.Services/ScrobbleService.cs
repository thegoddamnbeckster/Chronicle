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

            // Every scrobble (not just watched-threshold crossings) upserts the library
            // entry -- resume position needs to be current after every progress update,
            // not just once an item is finished. See UpsertLibraryStateAsync.
            var entry = await UpsertLibraryStateAsync(userId, mediaItemId, request.ProgressPercent, markedAsWatched, timestamp, ct);

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

            try
            {
                await _context.SaveChangesAsync(ct);
                return new ScrobbleResult(evt, markedAsWatched);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "user_libraries"))
            {
                // Two concurrent first-time scrobbles for the same (user, item) both found
                // no existing row and both tried to insert, tripping the unique index. The
                // whole SaveChangesAsync failed atomically, so evt's insert didn't land
                // either -- detach only the losing UserLibrary insert (not the whole change
                // set, which would also discard the still-valid evt add), reload the row the
                // other request just committed, and retry as an update against it.
                _context.Entry(entry).State = EntityState.Detached;
                entry = await UpsertLibraryStateAsync(userId, mediaItemId, request.ProgressPercent, markedAsWatched, timestamp, ct);
                await _context.SaveChangesAsync(ct);
                return new ScrobbleResult(evt, markedAsWatched);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "interaction_events"))
            {
                // Duplicate scrobble — same (user, item, timestamp) already recorded.
                // Detach only the failed event insert -- the UserLibrary upsert above is
                // still a valid, unrelated change and must survive this retry (unlike the
                // old ChangeTracker.Clear() here, which silently discarded it too) -- then
                // return the pre-existing event.
                _context.Entry(evt).State = EntityState.Detached;
                await _context.SaveChangesAsync(ct);
                var existing = await _context.InteractionEvents.AsNoTracking()
                    .FirstAsync(e => e.UserId == userId
                                  && e.MediaItemId == mediaItemId
                                  && e.Timestamp == timestamp, ct);
                return new ScrobbleResult(existing, markedAsWatched);
            }
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
            var found = await TryFindMediaItemAsync(request.Title, request.Year, request.MediaType, externalIds, ct);
            if (found != null)
            {
                await StoreExternalIdsAsync(found.Id, externalIds, ct);
                return found;
            }

            if (string.IsNullOrWhiteSpace(request.Title))
                throw new ArgumentException(
                    "Scrobble request has no MediaItemId and no Title to create a stub item from.");

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

        /// <summary>
        /// The "find" half of FindOrCreateMediaItemAsync, split out so
        /// GetResumeStateAsync can look an item up WITHOUT ever creating a stub --
        /// checking "is there a resume position for this?" must never itself create a
        /// media item for something Chronicle has never actually seen. Same external-ID-
        /// then-type-scoped-title/year matching either caller uses; returns null on no
        /// match instead of falling through to stub creation.
        /// </summary>
        private async Task<MediaItem?> TryFindMediaItemAsync(
            string? title, int? year, string? mediaType,
            IReadOnlyDictionary<string, string> externalIds, CancellationToken ct)
        {
            foreach (var (source, extId) in externalIds)
            {
                var match = await _context.MediaExternalIds
                    .Include(x => x.MediaItem)
                    .FirstOrDefaultAsync(x => x.Source == source.ToLowerInvariant() && x.ExternalId == extId, ct);
                if (match?.MediaItem != null)
                    return match.MediaItem;
            }

            if (string.IsNullOrWhiteSpace(title))
                return null;

            // Only attempts a title+year match when a media type can be confidently
            // resolved -- an omitted/unrecognized MediaType returns null rather than
            // matching unscoped (which would risk the exact cross-type collision this
            // scoping exists to prevent; see MediaItemMatcher docs).
            var matchTypeId = await MediaItemMatcher.TryResolveMediaTypeIdForMatchAsync(_context, mediaType, ct);
            if (!matchTypeId.HasValue)
                return null;

            return await MediaItemMatcher.FindByTitleYearAsync(_context, title, year, matchTypeId.Value, ct);
        }

        /// <summary>
        /// The cross-device "resume where I left off" lookup: resolves the item the same
        /// way a scrobble would (external ID, then type-scoped title/year -- never
        /// creates a stub, see TryFindMediaItemAsync), then returns its stored resume
        /// position if it has one. Null covers both "never seen this item" and "seen it,
        /// nothing to resume" identically -- a client checking whether to seek on
        /// playback start only ever needs to know "is there a position to resume from",
        /// not why not.
        /// </summary>
        public async Task<ResumeState?> GetResumeStateAsync(int userId, ResumeLookupRequest request, CancellationToken ct = default)
        {
            int mediaItemId;
            if (request.MediaItemId.HasValue)
            {
                // No need to fetch the MediaItem itself just to confirm it exists --
                // UserLibrary.MediaItemId is a real FK, so a row can only exist for a
                // media item that does; a nonexistent id falls through to the same
                // "entry == null -> null" result the MediaItems lookup would have given.
                mediaItemId = request.MediaItemId.Value;
            }
            else
            {
                var mediaItem = await TryFindMediaItemAsync(
                    request.Title, request.Year, request.MediaType, request.ExternalIds ?? new Dictionary<string, string>(), ct);
                if (mediaItem == null)
                    return null;
                mediaItemId = mediaItem.Id;
            }

            var entry = await _context.UserLibraries.AsNoTracking()
                .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId, ct);
            if (entry?.ResumePositionPercent is not double percent)
                return null;

            return new ResumeState(mediaItemId, percent, entry.ResumeUpdatedAt);
        }

        /// <summary>
        /// Sets the caller's UserRating for an item, resolving it the same way
        /// GetResumeStateAsync does (MediaItemId when known, else external-id/title/year
        /// match -- never a stub creation, since a rating is expected to arrive after the
        /// item was already scrobbled at least once). Creates the UserLibrary row if one
        /// doesn't exist yet (e.g. a rating submitted for an item scrobbled by a different
        /// device that never itself created a library entry here) so the rating has
        /// somewhere to live, same as UpsertLibraryStateAsync does for scrobbles.
        /// </summary>
        public async Task<RateResult> RateAsync(int userId, RateRequest request, CancellationToken ct = default)
        {
            if (request.Rating is < 1 or > 10)
                throw new ArgumentException("Rating must be between 1 and 10.");

            int mediaItemId;
            if (request.MediaItemId.HasValue)
            {
                mediaItemId = request.MediaItemId.Value;
                var exists = await _context.MediaItems.AnyAsync(m => m.Id == mediaItemId, ct);
                if (!exists)
                    throw new MediaNotFoundException(mediaItemId);
            }
            else
            {
                var item = await TryFindMediaItemAsync(
                    request.Title, request.Year, request.MediaType,
                    request.ExternalIds ?? new Dictionary<string, string>(), ct);
                if (item is null)
                    throw new MediaNotFoundException(request.Title ?? "(untitled)");
                mediaItemId = item.Id;
            }

            var entry = await _context.UserLibraries
                .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId, ct);

            if (entry is null)
            {
                entry = new UserLibrary
                {
                    UserId      = userId,
                    MediaItemId = mediaItemId,
                    // A rating implies the item was watched -- there's no scrobble history
                    // for it under this user yet (that's the only way this branch is
                    // reached), so mark it Completed rather than leaving it Unwatched with
                    // a rating attached, which the library view would show inconsistently.
                    Status      = LibraryStatus.Completed,
                    AddedAt     = DateTime.UtcNow,
                    UpdatedAt   = DateTime.UtcNow,
                    StartedAt   = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow,
                };
                _context.UserLibraries.Add(entry);
            }

            entry.UserRating = request.Rating;
            entry.UpdatedAt  = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return new RateResult(mediaItemId, request.Rating);
        }

        /// <summary>
        /// The scrobble protocol has no explicit "playback started/stopped" signal — every
        /// call is just a periodic percent-progress ping (see InteractionEvent's fields; there
        /// is no EventType/IsPlaying anywhere in this model). "Actively playing right now" is
        /// therefore inferred purely from recency: a session counts as live if its most recent
        /// event for a given device is within ActiveSessionWindow. This isn't an arbitrary
        /// guess — every real scrobbler client (Kodi, Audiobookshelf) defaults to a 30-second
        /// poll interval while playing, and the Kodi client specifically stops sending updates
        /// entirely while paused (onPlayBackPaused) — so the event stream itself already goes
        /// quiet within moments of a pause/stop, and a window a few multiples of that default
        /// interval is the correct margin against jitter/a slower-than-default configured
        /// interval, not a magic number.
        /// </summary>
        private static readonly TimeSpan ActiveSessionWindow = TimeSpan.FromSeconds(90);

        public async Task<IReadOnlyList<ActiveSession>> GetActiveSessionsAsync(int userId, CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow - ActiveSessionWindow;

            // Small result set by construction (a handful of devices, each with at most a
            // couple of events inside a 90-second window at a 30-second default poll rate) —
            // safe to reduce to "latest per device" in memory rather than push a group-by-then-
            // first-per-group query through the EF/SQLite translator.
            var recentEvents = await _context.InteractionEvents
                .Where(e => e.UserId == userId && e.Timestamp >= cutoff)
                .Include(e => e.MediaItem)
                .OrderByDescending(e => e.Timestamp)
                .ToListAsync(ct);

            var latestPerDevice = recentEvents
                .Where(e => !e.MarkedAsWatched && e.MediaItem != null)
                .GroupBy(e => e.DeviceName ?? "Unknown Device")
                .Select(g => g.First()); // already ordered desc by Timestamp above

            return latestPerDevice.Select(e =>
            {
                var runtimeMinutes = e.MediaItem!.RuntimeMinutes;
                var progress = e.ProgressPercent ?? 0;
                int? elapsedMinutes = runtimeMinutes is int rt
                    ? (int)Math.Round(rt * progress / 100.0)
                    : null;

                return new ActiveSession(
                    e.MediaItemId,
                    e.MediaItem.Name,
                    e.MediaItem.PosterUrl,
                    progress,
                    elapsedMinutes,
                    runtimeMinutes,
                    e.DeviceName,
                    e.Timestamp);
            }).ToList();
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

        /// <summary>
        /// Runs on every scrobble, not just watched-threshold crossings -- unlike the
        /// old UpdateLibraryStatusAsync this replaces, resume position needs to be
        /// current after every progress update, and an item the user only ever gets
        /// partway through should still show as "Watching", not stay absent from their
        /// library entirely until (if ever) they finish it. Returns the tracked entry so
        /// callers can detach it and retry precisely on a unique-constraint conflict
        /// without discarding unrelated pending changes (see ScrobbleAsync).
        /// </summary>
        private async Task<UserLibrary> UpsertLibraryStateAsync(
            int userId, int mediaItemId, double? progressPercent, bool markedAsWatched,
            DateTime timestamp, CancellationToken ct)
        {
            var entry = await _context.UserLibraries
                .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId, ct);

            if (entry == null)
            {
                entry = new UserLibrary
                {
                    UserId      = userId,
                    MediaItemId = mediaItemId,
                    Status      = markedAsWatched ? LibraryStatus.Completed : LibraryStatus.Watching,
                    AddedAt     = DateTime.UtcNow,
                    UpdatedAt   = DateTime.UtcNow,
                    StartedAt   = DateTime.UtcNow,
                    CompletedAt = markedAsWatched ? timestamp : null,
                };
                _context.UserLibraries.Add(entry);
            }
            else
            {
                // A scrobble crossing the watched threshold is a strong, unambiguous signal
                // regardless of whatever status the entry was already in (including a stale
                // "Watching" left behind by a version of this method that never made this
                // transition at all -- confirmed live 2026-08-24: entries scrobbled past
                // WatchedThreshold stayed "Unwatched" forever, since only the PlanToWatch->
                // Watching transition below existed and nothing ever set Completed).
                if (markedAsWatched)
                {
                    entry.Status      = LibraryStatus.Completed;
                    entry.CompletedAt = timestamp;
                    entry.StartedAt ??= DateTime.UtcNow;
                }
                else if (entry.Status is LibraryStatus.PlanToWatch or LibraryStatus.Unwatched)
                {
                    entry.Status    = LibraryStatus.Watching;
                    entry.StartedAt ??= DateTime.UtcNow;
                }

                // Refreshed on every real progress update, not just a status transition --
                // an actively-watched item should always read as recently active (Continue
                // Watching sorts on this), not go stale the moment its first scrobble lands.
                entry.UpdatedAt = DateTime.UtcNow;
            }

            // Cleared once watched -- an item you just finished has nothing left to
            // "resume", and leaving a stale percent behind would make a later rewatch
            // start with a bogus seek-ahead on whatever device picks it up next.
            if (markedAsWatched)
            {
                entry.ResumePositionPercent = null;
                entry.ResumeUpdatedAt       = null;
            }
            // A resume position is only ever overwritten by a scrobble at least as recent
            // as the one that set it -- otherwise a delayed/out-of-order scrobble (an
            // offline queue replay, a backfill import) with an older explicit Timestamp
            // could clobber a genuinely newer position with stale data.
            else if (progressPercent.HasValue
                     && (entry.ResumeUpdatedAt is not DateTime existing || timestamp >= existing))
            {
                entry.ResumePositionPercent = progressPercent.Value;
                entry.ResumeUpdatedAt       = timestamp;
            }

            return entry;
        }

        private static bool IsUniqueConstraintViolation(DbUpdateException ex, string table) =>
            ex.InnerException?.Message.Contains("UNIQUE constraint failed",
                StringComparison.OrdinalIgnoreCase) == true
            && ex.InnerException.Message.Contains(table, StringComparison.OrdinalIgnoreCase);
    }
}
