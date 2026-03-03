using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services
{
    public class MediaService : IMediaService
    {
        private readonly ChronicleDbContext _context;

        public MediaService(ChronicleDbContext context)
        {
            _context = context;
        }

        public async Task<MediaItem> CreateAsync(CreateMediaRequest request)
        {
            var item = new MediaItem
            {
                MediaTypeId = request.MediaTypeId,
                ParentId = request.ParentId,
                Name = request.Name,
                SortName = request.Name.TrimStart('T', 't', 'h', 'H', 'e', 'E', ' '),
                Year = request.Year,
                Overview = request.Overview,
                PosterUrl = request.PosterUrl,
                RuntimeMinutes = request.RuntimeMinutes,
                HierarchyLevel = request.HierarchyLevel,
                Number = request.Number,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync();

            await _context.Entry(item).Reference(i => i.MediaType).LoadAsync();

            return item;
        }

        public async Task<MediaItem?> GetByIdAsync(int id)
        {
            return await _context.MediaItems
                .Include(m => m.MediaType)
                .Include(m => m.ExternalIds)
                .FirstOrDefaultAsync(m => m.Id == id);
        }

        public async Task<IEnumerable<MediaItem>> SearchAsync(string query, int? mediaTypeId = null, int page = 1, int perPage = 20)
        {
            var q = _context.MediaItems
                .Include(m => m.MediaType)
                .Where(m => m.HierarchyLevel == 0);

            if (!string.IsNullOrWhiteSpace(query))
                q = q.Where(m => EF.Functions.Like(m.Name, $"%{query}%"));

            if (mediaTypeId.HasValue)
                q = q.Where(m => m.MediaTypeId == mediaTypeId.Value);

            return await q
                .OrderBy(m => m.SortName)
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToListAsync();
        }

        public async Task<IEnumerable<MediaItem>> GetChildrenAsync(int parentId)
        {
            return await _context.MediaItems
                .Where(m => m.ParentId == parentId)
                .OrderBy(m => m.Number)
                .ThenBy(m => m.Name)
                .ToListAsync();
        }

        public async Task<MediaItem> UpdateAsync(int id, UpdateMediaRequest request)
        {
            var item = await _context.MediaItems.FindAsync(id)
                ?? throw new MediaNotFoundException(id);

            if (request.Name != null) item.Name = request.Name;
            if (request.Year.HasValue) item.Year = request.Year;
            if (request.Overview != null) item.Overview = request.Overview;
            if (request.PosterUrl != null) item.PosterUrl = request.PosterUrl;
            if (request.RuntimeMinutes.HasValue) item.RuntimeMinutes = request.RuntimeMinutes;

            item.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return item;
        }

        public async Task DeleteAsync(int id)
        {
            var item = await _context.MediaItems.FindAsync(id)
                ?? throw new MediaNotFoundException(id);

            _context.MediaItems.Remove(item);
            await _context.SaveChangesAsync();
        }
    }
}
