using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers
{
    [ApiController]
    [Route("api/v1/media")]
    [Authorize]
    public class MediaController : ControllerBase
    {
        private readonly IMediaService _mediaService;
        private readonly IFileScanService _fileScanService;
        private readonly IMetadataRefreshService _refreshService;
        private readonly ChronicleDbContext _context;

        public MediaController(IMediaService mediaService, IFileScanService fileScanService,
            IMetadataRefreshService refreshService, ChronicleDbContext context)
        {
            _mediaService    = mediaService;
            _fileScanService = fileScanService;
            _refreshService  = refreshService;
            _context         = context;
        }

        [HttpGet("types")]
        public async Task<IActionResult> GetMediaTypes()
        {
            var types = await _context.MediaTypes
                .Where(t => t.IsActive)
                .OrderBy(t => t.DisplayName)
                .Select(t => new MediaTypeDto(t.Id, t.Name, t.DisplayName))
                .ToListAsync();
            return Ok(ApiResponse<List<MediaTypeDto>>.Ok(types));
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateMediaItemRequest request)
        {
            var item = await _mediaService.CreateAsync(new Chronicle.Services.CreateMediaRequest(
                request.MediaTypeId,
                request.ParentId,
                request.Name,
                request.Year,
                request.Overview,
                request.PosterUrl,
                request.RuntimeMinutes,
                request.HierarchyLevel,
                request.Number
            ));

            return CreatedAtAction(nameof(GetById), new { id = item.Id },
                ApiResponse<MediaItemDto>.Ok(ToDto(item)));
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id, CancellationToken ct)
        {
            var item = await _mediaService.GetByIdAsync(id);
            if (item == null)
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

            var refreshLogs = await _refreshService.GetRefreshLogsAsync(id, ct);

            // Build ancestor chain for breadcrumb navigation (root items have no ancestors)
            var ancestors = new List<AncestorDto>();
            var parentId = item.ParentId;
            while (parentId != null)
            {
                var ancestor = await _context.MediaItems
                    .Where(m => m.Id == parentId)
                    .Select(m => new { m.Id, m.Name, m.ParentId })
                    .FirstOrDefaultAsync(ct);
                if (ancestor == null) break;
                ancestors.Insert(0, new AncestorDto(ancestor.Id, ancestor.Name));
                parentId = ancestor.ParentId;
            }

            return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item, refreshLogs, ancestors.Count > 0 ? ancestors : null)));
        }

        [HttpGet("search")]
        public async Task<IActionResult> Search(
            [FromQuery] string? query,
            [FromQuery] int? mediaTypeId,
            [FromQuery] int page = 1,
            [FromQuery] int perPage = 20)
        {
            var results = await _mediaService.SearchAsync(query ?? string.Empty, mediaTypeId, page, perPage);
            var dtos = results.Select(m => ToDto(m)).ToList();
            return Ok(ApiResponse<List<MediaItemDto>>.Ok(dtos, new PaginationInfo(page, perPage, null)));
        }

        [HttpGet("{id:int}/children")]
        public async Task<IActionResult> GetChildren(int id)
        {
            var children = await _mediaService.GetChildrenAsync(id);
            return Ok(ApiResponse<List<MediaItemDto>>.Ok(children.Select(m => ToDto(m)).ToList()));
        }

        [HttpPatch("{id:int}")]
        public async Task<IActionResult> Update(int id, [FromBody] UpdateMediaItemRequest request)
        {
            try
            {
                var item = await _mediaService.UpdateAsync(id, new Chronicle.Services.UpdateMediaRequest(
                    request.Name, request.Year, request.Overview, request.PosterUrl, request.RuntimeMinutes));
                return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item)));
            }
            catch (MediaNotFoundException ex)
            {
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", ex.Message));
            }
        }

        [HttpPost("{id:int}/refresh")]
        public async Task<IActionResult> RefreshMetadata(int id, CancellationToken ct)
        {
            try
            {
                await _refreshService.RefreshItemAsync(id, ct);
                var item = await _mediaService.GetByIdAsync(id);
                if (item == null)
                    return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));
                var logs = await _refreshService.GetRefreshLogsAsync(id, ct);
                return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item, logs)));
            }
            catch (Exception ex)
            {
                return StatusCode(502, ApiResponse<MediaItemDto>.Fail("REFRESH_FAILED", ex.Message));
            }
        }

        /// <summary>
        /// Re-identifies a media item using a user-supplied TMDB reference.
        /// Accepts a bare numeric ID, a typed ID (movie:NNN / tv:NNN), or a full TMDB URL.
        /// Replaces name, year, overview, poster, and TMDB metadata in-place.
        /// </summary>
        [HttpPost("{id:int}/reidentify")]
        public async Task<IActionResult> Reidentify(int id, [FromBody] ReidentifyRequestDto dto, CancellationToken ct)
        {
            try
            {
                var item = await _fileScanService.ReidentifyAsync(id, dto.Input, ct);
                return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item)));
            }
            catch (KeyNotFoundException)
            {
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));
            }
            catch (NoProviderConfiguredException ex)
            {
                return Conflict(ApiResponse<MediaItemDto>.Fail("NO_PROVIDER_CONFIGURED", ex.Message));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ApiResponse<MediaItemDto>.Fail("INVALID_INPUT", ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(502, ApiResponse<MediaItemDto>.Fail("REIDENTIFY_FAILED", ex.Message));
            }
        }

        /// <summary>
        /// Removes a specific external ID (e.g. TMDB match) from a media item without
        /// deleting the item itself.  Also clears the corresponding metadata from MetadataJson.
        /// </summary>
        [HttpDelete("{id:int}/external-ids/{source}")]
        public async Task<IActionResult> ClearExternalId(int id, string source, CancellationToken ct)
        {
            var item = await _context.MediaItems
                .Include(m => m.ExternalIds)
                .FirstOrDefaultAsync(m => m.Id == id, ct);

            if (item is null)
                return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

            var toRemove = item.ExternalIds
                .Where(e => string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (toRemove.Count == 0)
                return NoContent(); // already absent — idempotent

            _context.MediaExternalIds.RemoveRange(toRemove);

            // Strip the provider's block from MetadataJson so stale data doesn't linger.
            if (!string.IsNullOrWhiteSpace(item.MetadataJson))
            {
                try
                {
                    var root = System.Text.Json.JsonSerializer.Deserialize<
                        System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(item.MetadataJson);
                    if (root is not null && root.Remove(source.ToLowerInvariant()))
                        item.MetadataJson = System.Text.Json.JsonSerializer.Serialize(root);
                }
                catch { /* malformed JSON — leave as-is */ }
            }

            ClearProviderMetadata(item);
            item.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return NoContent();
        }

        /// <summary>
        /// Suppresses auto-matching for a specific provider by storing a sentinel external ID.
        /// The metadata refresh service will skip this item for that provider permanently.
        /// Use ClearExternalId (DELETE) to un-suppress and allow auto-matching again.
        /// </summary>
        [HttpPost("{id:int}/suppress/{source}")]
        public async Task<IActionResult> SuppressMatch(int id, string source, CancellationToken ct)
        {
            var item = await _context.MediaItems
                .Include(m => m.ExternalIds)
                .FirstOrDefaultAsync(m => m.Id == id, ct);

            if (item is null)
                return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

            // Remove any existing ID for this source (real or previous suppress sentinel).
            var existing = item.ExternalIds
                .Where(e => string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _context.MediaExternalIds.RemoveRange(existing);

            // Also strip cached metadata for this provider.
            if (!string.IsNullOrWhiteSpace(item.MetadataJson))
            {
                try
                {
                    var root = System.Text.Json.JsonSerializer.Deserialize<
                        System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(item.MetadataJson);
                    if (root is not null && root.Remove(source.ToLowerInvariant()))
                        item.MetadataJson = System.Text.Json.JsonSerializer.Serialize(root);
                }
                catch { /* malformed JSON — leave as-is */ }
            }

            // Store the suppress sentinel.
            _context.MediaExternalIds.Add(new Chronicle.Core.Models.MediaExternalId
            {
                MediaItemId = item.Id,
                Source      = source.ToLowerInvariant(),
                ExternalId  = "__suppress__"
            });

            ClearProviderMetadata(item);
            item.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return NoContent();
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                await _mediaService.DeleteAsync(id);
                return NoContent();
            }
            catch (MediaNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", ex.Message));
            }
        }

        private static MediaItemDto ToDto(
            Chronicle.Core.Models.MediaItem m,
            IReadOnlyList<Chronicle.Core.Models.MediaItemRefreshLog>? refreshLogs = null,
            List<AncestorDto>? ancestors = null)
        {
            var (tmdb, fs) = ParseMetaJson(m.MetadataJson);
            var logDtos = refreshLogs?
                .Select(l => new RefreshLogDto(l.ProviderName, l.RefreshedAt, l.Succeeded, l.ErrorMessage))
                .ToList();
            return new MediaItemDto(
                m.Id,
                m.MediaTypeId,
                m.MediaType?.DisplayName ?? string.Empty,
                m.ParentId,
                m.Name,
                m.Year,
                m.Overview,
                m.PosterUrl,
                m.RuntimeMinutes,
                m.HierarchyLevel,
                m.Number,
                m.CreatedAt,
                m.UpdatedAt,
                m.ExternalIds.Select(e => new ExternalIdDto(e.Source, e.ExternalId)).ToList(),
                TmdbMeta: tmdb,
                FileScannerMeta: fs,
                RefreshLogs: logDtos,
                Ancestors: ancestors
            );
        }

        /// <summary>
        /// Clears all provider-supplied metadata from <paramref name="item"/>, restoring any
        /// file-scanner poster if available.  Called when the user explicitly removes a match.
        /// </summary>
        private static void ClearProviderMetadata(Chronicle.Core.Models.MediaItem item)
        {
            var (_, fs) = ParseMetaJson(item.MetadataJson);

            // Restore poster from file scanner if it has one, otherwise wipe it.
            item.PosterUrl      = fs?.NfoPosterUrl ?? fs?.LocalPosterPath;
            item.Overview       = null;
            item.RuntimeMinutes = null;
            // Name and Year are intentionally left as-is — they were either set from the
            // file scanner originally or manually edited; reverting them would be unexpected.
        }

        // Root wrapper for namespaced MetadataJson {"tmdb":{...},"fileScanner":{...}}
        private sealed record MediaMetaJsonRoot(TmdbMetaDto? Tmdb, FileScannerMetaDto? FileScanner);

        private static readonly System.Text.Json.JsonSerializerOptions _jsonOpts =
            new(System.Text.Json.JsonSerializerDefaults.Web);

        // TODO: Extract ParseMetaJson and MediaMetaJsonRoot to a shared Chronicle.API helper to remove this duplication
        private static (TmdbMetaDto? tmdb, FileScannerMetaDto? fs) ParseMetaJson(string? json)
        {
            if (json is null) return (null, null);
            try
            {
                var root = System.Text.Json.JsonSerializer.Deserialize<MediaMetaJsonRoot>(json, _jsonOpts);
                // Partitioned format: {"tmdb":{...},"fileScanner":{...}}
                // import-direct items have tmdb=null but fileScanner populated, so check either key.
                if (root is not null && (root.Tmdb is not null || root.FileScanner is not null))
                {
                    var fs = root.FileScanner;

                    // The hierarchical importer writes {"fileScanner":{"importedAt":...,"filePaths":[...]}}
                    // which deserialises into a FileScannerMetaDto with all-null fields because the
                    // property names don't match.  Extract filePaths[0] (or folderPath for parent
                    // items) from the raw JSON so the File Scanner card appears on the media detail page.
                    if (fs is not null && fs.FilePath is null && fs.LocalPosterPath is null && fs.NfoPosterUrl is null)
                    {
                        fs = TryExtractFilePathFromNewFormat(json) ?? fs;
                    }

                    // Suppress a completely empty FileScannerMetaDto — but keep it when ImportedAt
                    // is set (season/episode items that were scanner-imported but whose path hasn't
                    // been re-recorded yet still deserve a File Scanner card).
                    var fsOut = (fs?.FilePath is not null || fs?.LocalPosterPath is not null ||
                                 fs?.NfoPosterUrl is not null || fs?.ImportedAt is not null)
                        ? fs : null;

                    return (root.Tmdb, fsOut);
                }

                // Old flat format fallback (rating/genres/cast/directors at root level)
                var flat = System.Text.Json.JsonSerializer.Deserialize<TmdbMetaDto>(json, _jsonOpts);
                return (flat, null);
            }
            catch { return (null, null); }
        }

        /// <summary>
        /// Reads the raw MetadataJson and extracts a display path from the hierarchical
        /// group importer format.  For leaf items (seasons/episodes) this is the first
        /// entry in <c>filePaths</c>; for root show items (filePaths is empty) it falls
        /// back to the <c>folderPath</c> stored during import.
        /// </summary>
        private static FileScannerMetaDto? TryExtractFilePathFromNewFormat(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("fileScanner", out var sect))
                {
                    // Pull importedAt timestamp (present on all hierarchical-importer items)
                    DateTime? importedAt = null;
                    if (sect.TryGetProperty("importedAt", out var iat) &&
                        iat.TryGetDateTime(out var dt))
                        importedAt = dt;

                    // Leaf items (episodes/tracks): first entry in filePaths array
                    if (sect.TryGetProperty("filePaths", out var arr) &&
                        arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            var path = el.GetString();
                            if (!string.IsNullOrEmpty(path))
                                return new FileScannerMetaDto(path, null, null, importedAt);
                        }
                    }

                    // Parent items (shows/artists/seasons): filePaths is empty; fall back to
                    // folderPath which is stored for level-0 and level-1 groups.
                    if (sect.TryGetProperty("folderPath", out var fp))
                    {
                        var folderPath = fp.GetString();
                        if (!string.IsNullOrEmpty(folderPath))
                            return new FileScannerMetaDto(folderPath, null, null, importedAt);
                    }

                    // fileScanner section exists but no path recorded yet (older import).
                    // Still return a non-null DTO so the File Scanner card is shown.
                    if (importedAt.HasValue)
                        return new FileScannerMetaDto(null, null, null, importedAt);
                }
            }
            catch { /* ignore malformed JSON */ }
            return null;
        }
    }
}
