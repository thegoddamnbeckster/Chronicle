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

        public async Task<UserLibrary> AddAsync(int userId, AddToLibraryRequest request, CancellationToken ct = default)
        {
            var existing = await _context.UserLibraries
                .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == request.MediaItemId, ct);

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
            await _context.SaveChangesAsync(ct);

            await _context.Entry(entry).Reference(e => e.MediaItem).LoadAsync(ct);
            if (entry.MediaItem != null)
                await _context.Entry(entry.MediaItem).Reference(m => m.MediaType).LoadAsync(ct);

            return entry;
        }

        public async Task<IEnumerable<UserLibrary>> GetForUserAsync(int userId, LibraryStatus? status = null, int page = 1, int perPage = 20, bool rootOnly = false, bool includeMoviesInCollections = false, bool includeStubs = true, CancellationToken ct = default)
        {
            // The library is a shared catalog — every user sees ALL media items.
            // user_libraries rows carry per-user tracking data (status, rating, notes).
            // We auto-create a row (Unwatched) for any item the user hasn't tracked yet
            // so that PATCH/DELETE by UserLibrary.Id continue to work normally.

            // 1. Load all media items (applying rootOnly if requested)
            var itemsQuery = _context.MediaItems
                .Include(m => m.MediaType)
                .Include(m => m.ExternalIds)
                .AsQueryable();

            if (!includeStubs)
                itemsQuery = itemsQuery.Where(m => !m.IsStub);

            if (rootOnly)
            {
                if (includeMoviesInCollections)
                {
                    // Flatten movies hierarchy: treat Level 0 and Level 1 movies as peers.
                    // - Non-movie types: root items only (ParentId == null), unchanged.
                    // - Movies at Level 1: always included (these are films inside a collection).
                    // - Movies at Level 0: included only if they have no children (standalone films).
                    //   Level 0 items WITH children are collection containers — skip them so they
                    //   don't generate spurious library rows in the flat view.
                    var moviesTypeIds = await _context.MediaTypes
                        .Where(t => t.Name == "movies")
                        .Select(t => t.Id)
                        .ToListAsync(ct);

                    itemsQuery = itemsQuery.Where(m =>
                        // Non-movie root items are unchanged
                        (m.ParentId == null && !moviesTypeIds.Contains(m.MediaTypeId)) ||
                        // Movies at Level 1 (inside a collection)
                        (moviesTypeIds.Contains(m.MediaTypeId) && m.HierarchyLevel == 1) ||
                        // Standalone movies at Level 0 (not a collection container).
                        // The Any() translates to a single NOT EXISTS subquery in SQL — not
                        // a per-row round-trip — so this is one efficient query total.
                        (moviesTypeIds.Contains(m.MediaTypeId) && m.HierarchyLevel == 0 &&
                         !_context.MediaItems.Any(c => c.ParentId == m.Id)));
                }
                else
                {
                    itemsQuery = itemsQuery.Where(m => m.ParentId == null);
                }
            }

            var allItems = await itemsQuery.ToListAsync(ct);

            if (allItems.Count == 0)
                return [];

            // 2. Load existing user tracking rows for these items in one round-trip
            var allItemIds = allItems.Select(m => m.Id).ToList();
            var existingEntries = await _context.UserLibraries
                .Where(l => l.UserId == userId && allItemIds.Contains(l.MediaItemId))
                .ToDictionaryAsync(l => l.MediaItemId, ct);

            // 3. Auto-create tracking rows for items this user has never interacted with
            var toCreate = allItems.Where(m => !existingEntries.ContainsKey(m.Id)).ToList();
            if (toCreate.Count > 0)
            {
                foreach (var item in toCreate)
                {
                    var entry = new UserLibrary
                    {
                        UserId      = userId,
                        MediaItemId = item.Id,
                        Status      = LibraryStatus.Unwatched,
                        AddedAt     = DateTime.UtcNow,
                        UpdatedAt   = DateTime.UtcNow,
                    };
                    _context.UserLibraries.Add(entry);
                    existingEntries[item.Id] = entry;
                }
                await _context.SaveChangesAsync(ct);
            }

            // 4. Attach MediaItem navigation to each tracking row, apply status filter
            foreach (var item in allItems)
                existingEntries[item.Id].MediaItem = item;

            IEnumerable<UserLibrary> result = existingEntries.Values;

            if (status.HasValue)
                result = result.Where(e => e.Status == status.Value);

            result = result.OrderByDescending(e => e.UpdatedAt);

            if (page < 1) page = 1;

            // perPage == 0 means "no limit"; negative values are treated as no limit too.
            if (perPage > 0)
                result = result.Skip((page - 1) * perPage).Take(perPage);

            return result.ToList();
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

            // Delete the MediaItem and its entire tree so GetForUserAsync doesn't
            // re-create a UserLibrary row for it on the next library load.
            await DeleteMediaItemTreeAsync(entry.MediaItemId);
        }

        /// <summary>
        /// Recursively deletes a media item and all its descendants, together with every
        /// related row (UserLibrary entries for all users, enrichment rows, external IDs,
        /// interaction events, and list memberships).
        /// </summary>
        private async Task DeleteMediaItemTreeAsync(int mediaItemId)
        {
            // Delete all child items depth-first so FK constraints are satisfied.
            var childIds = await _context.MediaItems
                .Where(m => m.ParentId == mediaItemId)
                .Select(m => m.Id)
                .ToListAsync();

            foreach (var childId in childIds)
                await DeleteMediaItemTreeAsync(childId);

            // Remove all rows that reference this item.
            // Use RemoveRange+SaveChanges (not ExecuteDeleteAsync) so this works with
            // both SQLite and the EF InMemory provider used in integration tests.
            _context.UserLibraries.RemoveRange(
                await _context.UserLibraries.Where(l => l.MediaItemId == mediaItemId).ToListAsync());

            _context.MediaEnrichments.RemoveRange(
                await _context.MediaEnrichments.Where(e => e.MediaItemId == mediaItemId).ToListAsync());

            _context.MediaExternalIds.RemoveRange(
                await _context.MediaExternalIds.Where(e => e.MediaItemId == mediaItemId).ToListAsync());

            _context.InteractionEvents.RemoveRange(
                await _context.InteractionEvents.Where(e => e.MediaItemId == mediaItemId).ToListAsync());

            _context.MediaListItems.RemoveRange(
                await _context.MediaListItems.Where(li => li.MediaItemId == mediaItemId).ToListAsync());

            _context.MediaItems.RemoveRange(
                await _context.MediaItems.Where(m => m.Id == mediaItemId).ToListAsync());

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

            // Delete in dependency order to avoid FK violations.
            // Every table that references media_items or user_libraries must be cleared first.
            await _context.InteractionEvents.ExecuteDeleteAsync(ct);
            await _context.UserLibraries.ExecuteDeleteAsync(ct);
            await _context.MediaExternalIds.ExecuteDeleteAsync(ct);
            await _context.MediaEnrichments.ExecuteDeleteAsync(ct);
            await _context.MediaCredits.ExecuteDeleteAsync(ct);
            await _context.MediaListItems.ExecuteDeleteAsync(ct);
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

            await _context.InteractionEvents
                .Where(e => allIds.Contains(e.MediaItemId))
                .ExecuteDeleteAsync(ct);

            await _context.UserLibraries
                .Where(l => allIds.Contains(l.MediaItemId))
                .ExecuteDeleteAsync(ct);

            await _context.MediaExternalIds
                .Where(e => allIds.Contains(e.MediaItemId))
                .ExecuteDeleteAsync(ct);

            await _context.MediaEnrichments
                .Where(e => allIds.Contains(e.MediaItemId))
                .ExecuteDeleteAsync(ct);

            await _context.MediaCredits
                .Where(c => allIds.Contains(c.MediaItemId))
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
