using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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

            q = q.OrderByDescending(l => l.UpdatedAt);

            // perPage == 0 means "no limit" — return the full result set
            if (perPage > 0)
                q = q.Skip((page - 1) * perPage).Take(perPage);

            return await q.ToListAsync(ct);
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

            if (entries.Count == 0) return 0;

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
            var descendants = await GetAllDescendantIdsAsync(exclusiveIds, ct);
            var allToDelete = new HashSet<int>(exclusiveIds);
            allToDelete.UnionWith(descendants);

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

        public async Task<int> NuclearResetAsync(string confirmationToken, CancellationToken ct = default)
        {
            if (confirmationToken != "RESET")
                throw new ArgumentException("Confirmation token must be exactly 'RESET'.");

            // Count before deletion for the return value
            var count = await _context.UserLibraries.CountAsync(ct);

            // Delete in dependency order to avoid FK violations
            await _context.InteractionEvents.ExecuteDeleteAsync(ct);
            await _context.UserLibraries.ExecuteDeleteAsync(ct);
            await _context.MediaExternalIds.ExecuteDeleteAsync(ct);
            await _context.MediaItems.ExecuteDeleteAsync(ct);

            return count;
        }

        public async Task<int> ClearScannerDataAsync(CancellationToken ct = default)
        {
            // Scanner items are identified by either:
            //   (a) MetadataJson IS NULL — old flat imports created before SerializeMetadata was applied
            //   (b) MetadataJson contains "fileScanner" — new hierarchical imports
            // TMDB-enriched items (MetadataJson has "tmdb" key but no "fileScanner") are intentionally
            // excluded — they have useful metadata the user may want to keep.
            // Only ROOT items are targeted; children are removed automatically via ON DELETE CASCADE.
            var scannerItemIds = await _context.MediaItems
                .Where(m => m.ParentId == null
                         && (m.MetadataJson == null || m.MetadataJson.Contains("\"fileScanner\"")))
                .Select(m => m.Id)
                .ToListAsync(ct);

            if (scannerItemIds.Count == 0) return 0;

            // Delete dependent rows first (EF bulk delete does not trigger SQLite cascades
            // via the ORM layer, so we must remove FK-referencing rows manually).
            // user_libraries and media_external_ids reference media_items; interaction_events
            // reference user_libraries. We collect child IDs too so child library entries
            // are also removed before we delete the root items (which would cascade-delete
            // the child media_items rows, potentially leaving orphan library entries
            // in databases where FK enforcement is off).
            var allDescendantIds = await GetAllDescendantIdsAsync(scannerItemIds, ct);
            var allIds = scannerItemIds.Concat(allDescendantIds).Distinct().ToList();

            await _context.UserLibraries
                .Where(l => allIds.Contains(l.MediaItemId))
                .ExecuteDeleteAsync(ct);

            await _context.MediaExternalIds
                .Where(e => allIds.Contains(e.MediaItemId))
                .ExecuteDeleteAsync(ct);

            // Deleting root items cascades to child media_items automatically.
            var count = await _context.MediaItems
                .Where(m => scannerItemIds.Contains(m.Id))
                .ExecuteDeleteAsync(ct);

            return count;
        }

        /// <summary>
        /// Returns the IDs of all descendants of <paramref name="rootIds"/> (not including the root IDs themselves).
        /// Uses level-by-level IN-clause batching to avoid N+1 queries.
        /// </summary>
        private async Task<List<int>> GetAllDescendantIdsAsync(IEnumerable<int> rootIds, CancellationToken ct)
        {
            var result = new List<int>();
            var currentLevel = rootIds.ToList();

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
