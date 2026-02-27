using Chronicle.Core.Models;

namespace Chronicle.Services
{
    public record CreateMediaRequest(
        int MediaTypeId,
        int? ParentId,
        string Name,
        int? Year,
        string? Overview,
        string? PosterUrl,
        int? RuntimeMinutes,
        int HierarchyLevel,
        int? Number
    );

    public record UpdateMediaRequest(
        string? Name,
        int? Year,
        string? Overview,
        string? PosterUrl,
        int? RuntimeMinutes
    );

    public interface IMediaService
    {
        Task<MediaItem> CreateAsync(CreateMediaRequest request);
        Task<MediaItem?> GetByIdAsync(int id);
        Task<IEnumerable<MediaItem>> SearchAsync(string query, int? mediaTypeId = null, int page = 1, int perPage = 20);
        Task<IEnumerable<MediaItem>> GetChildrenAsync(int parentId);
        Task<MediaItem> UpdateAsync(int id, UpdateMediaRequest request);
        Task DeleteAsync(int id);
    }
}
