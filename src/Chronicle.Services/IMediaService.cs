using Chronicle.Core.Models;
using System.Threading;

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
        Task<IEnumerable<MediaItem>> SearchAsync(string query, int? mediaTypeId = null, int page = 1, int perPage = 20, bool allLevels = false);
        Task<IEnumerable<MediaItem>> GetChildrenAsync(int parentId);
        Task<MediaItem> UpdateAsync(int id, UpdateMediaRequest request);
        Task DeleteAsync(int id);

        /// <summary>
        /// Changes the media type of <paramref name="id"/> and all its descendants
        /// (cascade), resetting all enrichment data, external IDs, and metadata JSON.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="id"/> is not a root item, or when the target
        /// type's hierarchy depth is incompatible with the existing item tree.
        /// </exception>
        Task ChangeTypeAsync(int id, int targetMediaTypeId, CancellationToken ct = default);
    }
}
