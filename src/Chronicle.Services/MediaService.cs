using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services
{
    public class MediaService : IMediaService
    {
        // Strips leading "The ", "A ", "An " (case-insensitive) for sort-name generation.
        private static readonly Regex _articlePrefixRe =
            new(@"^(?:The|A|An)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ChronicleDbContext _context;
        private readonly IPluginRegistry    _pluginRegistry;

        public MediaService(ChronicleDbContext context, IPluginRegistry pluginRegistry)
        {
            _context        = context;
            _pluginRegistry = pluginRegistry;
        }

        public async Task<MediaItem> CreateAsync(CreateMediaRequest request, CancellationToken ct = default)
        {
            var item = new MediaItem
            {
                MediaTypeId    = request.MediaTypeId,
                ParentId       = request.ParentId,
                Name           = request.Name,
                SortName       = _articlePrefixRe.Replace(request.Name, string.Empty),
                NormalizedName = MediaItemNormalizer.NormalizeName(request.Name),
                Year           = request.Year,
                Overview       = request.Overview,
                PosterUrl      = request.PosterUrl,
                RuntimeMinutes = request.RuntimeMinutes,
                HierarchyLevel = request.HierarchyLevel,
                Number         = request.Number,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow
            };

            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync(ct);

            await _context.Entry(item).Reference(i => i.MediaType).LoadAsync(ct);

            return item;
        }

        public async Task<MediaItem?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            return await _context.MediaItems
                .Include(m => m.MediaType)
                .Include(m => m.ExternalIds)
                .Include(m => m.Aliases)
                .Include(m => m.MergesAsWinner)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
        }

        public async Task<IEnumerable<MediaItem>> SearchAsync(string query, int? mediaTypeId = null, int page = 1, int perPage = 20, bool allLevels = false, CancellationToken ct = default)
        {
            var q = _context.MediaItems
                .Include(m => m.MediaType)
                .AsQueryable();

            if (!allLevels)
                q = q.Where(m => m.HierarchyLevel == 0);

            if (!string.IsNullOrWhiteSpace(query))
                q = q.Where(m =>
                    EF.Functions.Like(m.Name, $"%{query}%") ||
                    m.Aliases.Any(a => EF.Functions.Like(a.Alias, $"%{query}%")));

            if (mediaTypeId.HasValue)
                q = q.Where(m => m.MediaTypeId == mediaTypeId.Value);

            if (page < 1) page = 1;

            return await q
                .OrderBy(m => m.SortName)
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToListAsync(ct);
        }

        public async Task<IEnumerable<MediaItem>> GetChildrenAsync(int parentId, CancellationToken ct = default)
        {
            return await _context.MediaItems
                .Where(m => m.ParentId == parentId)
                .OrderBy(m => m.Number)
                .ThenBy(m => m.Name)
                .ToListAsync(ct);
        }

        public async Task<MediaItem> UpdateAsync(int id, UpdateMediaRequest request, CancellationToken ct = default)
        {
            var item = await _context.MediaItems.FindAsync([id], ct)
                ?? throw new MediaNotFoundException(id);

            if (request.Name != null) item.Name = request.Name;
            if (request.Year.HasValue) item.Year = request.Year;
            if (request.Overview != null) item.Overview = request.Overview;
            if (request.PosterUrl != null) item.PosterUrl = request.PosterUrl;
            if (request.RuntimeMinutes.HasValue) item.RuntimeMinutes = request.RuntimeMinutes;

            item.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return item;
        }

        public async Task DeleteAsync(int id, CancellationToken ct = default)
        {
            var item = await _context.MediaItems.FindAsync([id], ct)
                ?? throw new MediaNotFoundException(id);

            _context.MediaItems.Remove(item);
            await _context.SaveChangesAsync(ct);
        }

        public async Task ChangeTypeAsync(int id, int targetMediaTypeId, CancellationToken ct = default)
        {
            var item = await _context.MediaItems.FindAsync([id], ct)
                ?? throw new MediaNotFoundException(id);

            if (item.HierarchyLevel != 0)
                throw new InvalidOperationException(
                    $"Only root items can have their media type changed. " +
                    $"Item {id} is a child item (parentId={item.ParentId}). " +
                    $"Change the type of the root item instead.");

            var targetType = await _context.Set<MediaType>().FindAsync([targetMediaTypeId], ct)
                ?? throw new InvalidOperationException($"Media type {targetMediaTypeId} not found.");

            // BFS collect all descendant IDs
            var allIds = new List<int> { id };
            var queue = new Queue<int>();
            int maxDepth = 0;
            queue.Enqueue(id);
            while (queue.Count > 0)
            {
                var parentId = queue.Dequeue();
                var children = await _context.MediaItems
                    .Where(m => m.ParentId == parentId)
                    .Select(m => new { m.Id, m.HierarchyLevel })
                    .ToListAsync(ct);
                foreach (var c in children)
                {
                    allIds.Add(c.Id);
                    queue.Enqueue(c.Id);
                    if (c.HierarchyLevel > maxDepth) maxDepth = c.HierarchyLevel;
                }
            }

            var actualDepth = maxDepth + 1;
            if (targetType.HierarchyLevels < actualDepth)
                throw new InvalidOperationException(
                    $"Target type '{targetType.DisplayName}' supports {targetType.HierarchyLevels} level(s), " +
                    $"but this item tree has {actualDepth} level(s). Types are incompatible.");

            var items = await _context.MediaItems.Where(m => allIds.Contains(m.Id)).ToListAsync(ct);
            foreach (var i in items)
            {
                i.MediaTypeId  = targetMediaTypeId;
                // Preserve the fileScanner section — it describes the physical file on disk,
                // not the content type, so it should survive a type change.
                i.MetadataJson = PreserveFileScannerJson(i.MetadataJson);
                i.UpdatedAt    = DateTime.UtcNow;
            }

            var externalIds = await _context.MediaExternalIds.Where(e => allIds.Contains(e.MediaItemId)).ToListAsync(ct);
            _context.MediaExternalIds.RemoveRange(externalIds);

            var enrichments = await _context.MediaEnrichments.Where(e => allIds.Contains(e.MediaItemId)).ToListAsync(ct);
            _context.MediaEnrichments.RemoveRange(enrichments);

            // Create new Pending enrichment rows for each installed metadata plugin that
            // supports the new media type.  This ensures the enrichment stats table shows
            // the correct pending count immediately and that background tasks pick up the
            // items on the next run — without requiring the user to manually refresh each item.
            var newTypeName = targetType.Name;
            var toAdd       = new List<MediaItemEnrichment>();
            foreach (var (pluginId, provider, _) in _pluginRegistry.GetMetadataProviderEntries())
            {
                var supported = provider.GetSupportedMediaTypes()
                    .Any(t => string.Equals(
                        NormalizeTypeName(t.MediaTypeName),
                        NormalizeTypeName(newTypeName),
                        StringComparison.OrdinalIgnoreCase));
                if (!supported) continue;

                foreach (var itemId in allIds)
                {
                    toAdd.Add(new MediaItemEnrichment
                    {
                        MediaItemId = itemId,
                        PluginId    = pluginId,
                        Status      = EnrichmentStatus.Pending,
                        MaxRetries  = 3,
                    });
                }
            }
            if (toAdd.Count > 0)
                _context.MediaEnrichments.AddRange(toAdd);

            await _context.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Normalises a media-type name for plugin-compatibility matching.
        /// Mirrors the logic in MetadataEnrichmentService: "movies" → "movie".
        /// </summary>
        private static string NormalizeTypeName(string name) =>
            name.Equals("movies", StringComparison.OrdinalIgnoreCase) ? "movie" : name.ToLowerInvariant();

        /// <summary>
        /// Strips all plugin metadata from a MetadataJson blob while keeping the
        /// <c>fileScanner</c> section intact. The file scanner entry describes the
        /// physical file on disk and is independent of media type.
        /// Returns <c>null</c> if there is no file scanner data to preserve.
        /// </summary>
        private static string? PreserveFileScannerJson(string? metadataJson)
        {
            if (string.IsNullOrWhiteSpace(metadataJson))
                return null;

            try
            {
                var root = JsonNode.Parse(metadataJson)?.AsObject();
                if (root is null) return null;

                if (!root.ContainsKey("fileScanner"))
                    return null;

                var preserved = new JsonObject
                {
                    ["fileScanner"] = root["fileScanner"]?.DeepClone()
                };
                return preserved.ToJsonString();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
