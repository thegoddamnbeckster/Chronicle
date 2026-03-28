using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
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
        private readonly IMetadataEnrichmentService _enrichment;
        private readonly ChronicleDbContext _context;

        public MediaController(IMediaService mediaService, IFileScanService fileScanService,
            IMetadataEnrichmentService enrichment, ChronicleDbContext context)
        {
            _mediaService    = mediaService;
            _fileScanService = fileScanService;
            _enrichment      = enrichment;
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

            var enrichmentRecords = await _enrichment.GetEnrichmentRecordsAsync(id, ct);
            var ancestors = await BuildAncestorsAsync(item.ParentId, ct);
            var enrichmentStatusDict = await GetEnrichmentStatusDictAsync(id, ct);

            return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item, enrichmentRecords, ancestors.Count > 0 ? ancestors : null, enrichmentStatusDict)));
        }

        [HttpGet("search")]
        public async Task<IActionResult> Search(
            [FromQuery] string? query,
            [FromQuery] int? mediaTypeId,
            [FromQuery] int page = 1,
            [FromQuery] int perPage = 20,
            [FromQuery] bool allLevels = false)
        {
            var results = await _mediaService.SearchAsync(query ?? string.Empty, mediaTypeId, page, perPage, allLevels);
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
                await _enrichment.EnrichItemAsync(id,
                    new EnrichmentOptions(EnrichmentMode.Force, Cascade: true), ct);
                var item = await _mediaService.GetByIdAsync(id);
                if (item == null)
                    return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));
                var enrichmentRecords = await _enrichment.GetEnrichmentRecordsAsync(id, ct);
                var ancestors = await BuildAncestorsAsync(item.ParentId, ct);
                var enrichmentStatuses = await GetEnrichmentStatusDictAsync(id, ct);
                return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item, enrichmentRecords, ancestors.Count > 0 ? ancestors : null, enrichmentStatuses)));
            }
            catch (Exception ex)
            {
                return StatusCode(502, ApiResponse<MediaItemDto>.Fail("REFRESH_FAILED", ex.Message));
            }
        }

        /// <summary>
        /// Refreshes metadata for a single item from a specific plugin.
        /// If a body with <c>input</c> is supplied, performs a Fix Match (overrides external ID lookup).
        /// If no body / null input, re-fetches using the item's existing stored external ID.
        /// </summary>
        [HttpPost("{id:int}/refresh/{pluginId}")]
        public async Task<IActionResult> RefreshForPlugin(
            int id,
            string pluginId,
            [FromBody] PluginRefreshRequestDto? dto,
            CancellationToken ct)
        {
            try
            {
                var opts = new EnrichmentOptions(
                    EnrichmentMode.Force,
                    IdOverride: string.IsNullOrWhiteSpace(dto?.Input) ? null : dto.Input.Trim(),
                    Cascade: false);
                await _enrichment.EnrichItemAsync(id, pluginId, opts, ct);
                var item = await _mediaService.GetByIdAsync(id);
                if (item == null)
                    return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));
                var enrichmentRecords = await _enrichment.GetEnrichmentRecordsAsync(id, ct);
                var ancestors = await BuildAncestorsAsync(item.ParentId, ct);
                var enrichmentStatuses = await GetEnrichmentStatusDictAsync(id, ct);
                return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item, enrichmentRecords, ancestors.Count > 0 ? ancestors : null, enrichmentStatuses)));
            }
            catch (MediaNotFoundException ex)
            {
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<MediaItemDto>.Fail("PLUGIN_NOT_FOUND", ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(502, ApiResponse<MediaItemDto>.Fail("REFRESH_FAILED", ex.Message));
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

        private async Task<List<AncestorDto>> BuildAncestorsAsync(int? parentId, CancellationToken ct)
        {
            var ancestors = new List<AncestorDto>();
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
            return ancestors;
        }

        private async Task<Dictionary<string, string>?> GetEnrichmentStatusDictAsync(int mediaItemId, CancellationToken ct)
        {
            var rows = await _context.MediaEnrichments
                .Where(e => e.MediaItemId == mediaItemId)
                .Select(e => new { e.PluginId, Status = e.Status.ToString() })
                .ToListAsync(ct);
            return rows.Count > 0 ? rows.ToDictionary(e => e.PluginId, e => e.Status) : null;
        }

        private static MediaItemDto ToDto(
            Chronicle.Core.Models.MediaItem m,
            IReadOnlyList<EnrichmentRecord>? enrichmentRecords = null,
            List<AncestorDto>? ancestors = null,
            Dictionary<string, string>? enrichmentStatuses = null)
        {
            var (fs, pluginMeta) = ParseMetaJson(m.MetadataJson);
            // Map enrichment records to RefreshLogDto for frontend compatibility
            var logDtos = enrichmentRecords?
                .Where(r => r.LastCompletedAt.HasValue || r.ErrorMessage is not null)
                .Select(r => new RefreshLogDto(
                    r.PluginId,
                    r.LastCompletedAt ?? DateTime.UtcNow,
                    r.Status == EnrichmentStatus.Completed,
                    r.ErrorMessage))
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
                FileScannerMeta: fs,
                PluginMetadata: pluginMeta?.Count > 0 ? pluginMeta : null,
                RefreshLogs: logDtos,
                Ancestors: ancestors,
                EnrichmentStatuses: enrichmentStatuses
            );
        }

        /// <summary>
        /// Clears all provider-supplied metadata from <paramref name="item"/>, restoring any
        /// file-scanner poster if available.  Called when the user explicitly removes a match.
        /// </summary>
        private static void ClearProviderMetadata(Chronicle.Core.Models.MediaItem item)
        {
            var (fs, _) = ParseMetaJson(item.MetadataJson);

            // Restore poster from file scanner if it has one, otherwise wipe it.
            item.PosterUrl      = fs?.NfoPosterUrl ?? fs?.LocalPosterPath;
            item.Overview       = null;
            item.RuntimeMinutes = null;
            // Name and Year are intentionally left as-is — they were either set from the
            // file scanner originally or manually edited; reverting them would be unexpected.
        }

        // "fileScanner" is the only first-class key — it gets its own typed DTO field.
        // All plugin metadata (TMDB, MusicBrainz, etc.) flows through PluginMetadata
        // keyed by full plugin ID, so Chronicle never needs to know any plugin's data shape.
        private static readonly HashSet<string> _firstClassKeys =
            new(StringComparer.OrdinalIgnoreCase) { "fileScanner" };

        private static readonly System.Text.Json.JsonSerializerOptions _jsonOpts =
            new(System.Text.Json.JsonSerializerDefaults.Web);

        private static (FileScannerMetaDto? fs,
                        Dictionary<string, System.Text.Json.JsonElement>? pluginMeta)
            ParseMetaJson(string? json)
        {
            if (json is null) return (null, null);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, null);

                FileScannerMetaDto? fs = null;
                if (root.TryGetProperty("fileScanner", out var fsEl))
                    fs = System.Text.Json.JsonSerializer.Deserialize<FileScannerMetaDto>(fsEl.GetRawText(), _jsonOpts);

                // The hierarchical importer writes {"fileScanner":{"importedAt":...,"filePaths":[...]}}
                // which deserialises into a FileScannerMetaDto with all-null fields because the
                // property names don't match.  Extract filePaths[0] (or folderPath for parent
                // items) from the raw JSON so the File Scanner card appears on the media detail page.
                if (fs is not null && fs.FilePath is null && fs.LocalPosterPath is null && fs.NfoPosterUrl is null)
                    fs = TryExtractFilePathFromNewFormat(json) ?? fs;

                // Suppress a completely empty FileScannerMetaDto — but keep it when ImportedAt
                // is set (scanner-imported items that haven't re-recorded the path yet).
                var fsOut = (fs?.FilePath is not null || fs?.LocalPosterPath is not null ||
                             fs?.NfoPosterUrl is not null || fs?.ImportedAt is not null)
                    ? fs : null;

                // All non-fileScanner keys are plugin metadata — pass raw JsonElements so
                // the API remains agnostic about each plugin's internal data shape.
                Dictionary<string, System.Text.Json.JsonElement>? pluginMeta = null;
                foreach (var prop in root.EnumerateObject())
                {
                    if (_firstClassKeys.Contains(prop.Name)) continue;
                    pluginMeta ??= new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
                    // Normalize object keys to camelCase so TypeScript can read them directly
                    pluginMeta[prop.Name] = NormalizeToCamelCase(prop.Value);
                }

                return (fsOut, pluginMeta);
            }
            catch { return (null, null); }
        }

        /// <summary>
        /// Recursively rewrites all JSON object keys to camelCase (lowercases the first
        /// character) so that plugin metadata serialised with PascalCase conventions
        /// (the .NET default) can be read directly by TypeScript interfaces.
        /// Arrays and primitives are returned unchanged (cloned from the source document).
        /// </summary>
        private static System.Text.Json.JsonElement NormalizeToCamelCase(System.Text.Json.JsonElement element)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                {
                    var ms = new System.IO.MemoryStream();
                    using var w = new System.Text.Json.Utf8JsonWriter(ms);
                    w.WriteStartObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        var key = prop.Name.Length > 0
                            ? char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]
                            : prop.Name;
                        w.WritePropertyName(key);
                        WriteNormalized(w, prop.Value);
                    }
                    w.WriteEndObject();
                    w.Flush();
                    ms.Position = 0;
                    using var doc = System.Text.Json.JsonDocument.Parse(ms);
                    return doc.RootElement.Clone();
                }
                default:
                    return element.Clone();
            }
        }

        private static void WriteNormalized(System.Text.Json.Utf8JsonWriter w, System.Text.Json.JsonElement element)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    w.WriteStartObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        var key = prop.Name.Length > 0
                            ? char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]
                            : prop.Name;
                        w.WritePropertyName(key);
                        WriteNormalized(w, prop.Value);
                    }
                    w.WriteEndObject();
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                        WriteNormalized(w, item);
                    w.WriteEndArray();
                    break;
                default:
                    element.WriteTo(w);
                    break;
            }
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
