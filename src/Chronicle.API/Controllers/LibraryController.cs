using System.Security.Claims;
using System.Text.Json;
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
    [Route("api/v1/library")]
    [Authorize]
    public class LibraryController : ControllerBase
    {
        private readonly ILibraryService _libraryService;
        private readonly ChronicleDbContext _context;
        private readonly IUserService _userService;

        public LibraryController(ILibraryService libraryService, ChronicleDbContext context, IUserService userService)
        {
            _libraryService = libraryService;
            _context = context;
            _userService = userService;
        }

        [HttpPost]
        public async Task<IActionResult> Add([FromBody] AddToLibraryRequestDto request)
        {
            var userId = GetUserId();
            if (!Enum.TryParse<LibraryStatus>(request.Status, out var status))
                return BadRequest(ApiResponse<LibraryEntryDto>.Fail("INVALID_STATUS", $"Unknown status '{request.Status}'."));

            var entry = await _libraryService.AddAsync(userId, new AddToLibraryRequest(request.MediaItemId, status));
            return Ok(ApiResponse<LibraryEntryDto>.Ok(ToDto(entry)));
        }

        [HttpGet]
        public async Task<IActionResult> GetLibrary(
            [FromQuery] string? status,
            [FromQuery] int page = 1,
            [FromQuery] int perPage = 20,
            [FromQuery] bool rootOnly = false,
            [FromQuery] bool includeMoviesInCollections = false,
            CancellationToken ct = default)
        {
            var userId = GetUserId();
            LibraryStatus? parsedStatus = null;

            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<LibraryStatus>(status, out var s))
                    return BadRequest(ApiResponse<List<LibraryEntryDto>>.Fail("INVALID_STATUS", $"Unknown status '{status}'."));
                parsedStatus = s;
            }

            var prefs = await _userService.GetPreferencesAsync(userId);
            var includeStubs = prefs.CreateCollectionStubs ?? true;

            var entries = await _libraryService.GetForUserAsync(userId, parsedStatus, page, perPage, rootOnly, includeMoviesInCollections, includeStubs, ct);

            // Batch-fetch descendant MetadataJson for all root items in two queries
            // (direct children + grandchildren) to avoid N+1 when computing physical-file flags.
            var rootIds = entries
                .Where(e => e.MediaItem != null)
                .Select(e => e.MediaItem!.Id)
                .ToList();

            Dictionary<int, List<string?>> directChildrenByRoot = new();
            Dictionary<int, List<string?>> grandchildrenByRoot = new();

            if (rootIds.Count > 0)
            {
                var directChildren = await _context.MediaItems
                    .Where(m => m.ParentId != null && rootIds.Contains(m.ParentId.Value))
                    .Select(m => new { m.Id, m.ParentId, m.MetadataJson })
                    .ToListAsync(ct);

                foreach (var c in directChildren)
                {
                    var pid = c.ParentId!.Value;
                    if (!directChildrenByRoot.TryGetValue(pid, out var list))
                        directChildrenByRoot[pid] = list = new List<string?>();
                    list.Add(c.MetadataJson);
                }

                var directChildIds = directChildren.Select(c => c.Id).ToList();
                if (directChildIds.Count > 0)
                {
                    // Map grandchildren back to the root item via the direct child's parent.
                    var directChildToRoot = directChildren.ToDictionary(c => c.Id, c => c.ParentId!.Value);

                    var grandchildren = await _context.MediaItems
                        .Where(m => m.ParentId != null && directChildIds.Contains(m.ParentId.Value))
                        .Select(m => new { m.ParentId, m.MetadataJson })
                        .ToListAsync(ct);

                    foreach (var gc in grandchildren)
                    {
                        if (!directChildToRoot.TryGetValue(gc.ParentId!.Value, out var rootId)) continue;
                        if (!grandchildrenByRoot.TryGetValue(rootId, out var list))
                            grandchildrenByRoot[rootId] = list = new List<string?>();
                        list.Add(gc.MetadataJson);
                    }
                }
            }

            var dtos = entries.Select(e =>
            {
                List<string?>? dc = null;
                List<string?>? gc = null;
                if (e.MediaItem != null)
                {
                    directChildrenByRoot.TryGetValue(e.MediaItem.Id, out dc);
                    grandchildrenByRoot.TryGetValue(e.MediaItem.Id, out gc);
                }
                return ToDto(e, dc, gc);
            }).ToList();

            return Ok(ApiResponse<List<LibraryEntryDto>>.Ok(dtos, new PaginationInfo(page, perPage, null)));
        }

        [HttpPatch("{id:int}")]
        public async Task<IActionResult> Update(int id, [FromBody] UpdateLibraryRequestDto request)
        {
            var userId = GetUserId();
            LibraryStatus? parsedStatus = null;

            if (!string.IsNullOrEmpty(request.Status))
            {
                if (!Enum.TryParse<LibraryStatus>(request.Status, out var s))
                    return BadRequest(ApiResponse<LibraryEntryDto>.Fail("INVALID_STATUS", $"Unknown status '{request.Status}'."));
                parsedStatus = s;
            }

            try
            {
                var entry = await _libraryService.UpdateAsync(userId, id, new UpdateLibraryRequest(parsedStatus, request.UserRating, request.Notes));
                return Ok(ApiResponse<LibraryEntryDto>.Ok(ToDto(entry)));
            }
            catch (LibraryEntryNotFoundException ex)
            {
                return NotFound(ApiResponse<LibraryEntryDto>.Fail("ENTRY_NOT_FOUND", ex.Message));
            }
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Remove(int id)
        {
            var userId = GetUserId();
            try
            {
                await _libraryService.RemoveAsync(userId, id);
                return NoContent();
            }
            catch (LibraryEntryNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail("ENTRY_NOT_FOUND", ex.Message));
            }
        }

        [HttpDelete("all")]
        public async Task<IActionResult> ClearAll(CancellationToken ct)
        {
            var userId = GetUserId();
            var removed = await _libraryService.ClearAllAsync(userId, ct);
            return Ok(ApiResponse<object>.Ok(new { removedItems = removed }));
        }

        [HttpPost("reset")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> NuclearReset(
            [FromBody] NuclearResetRequestDto request,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.ConfirmationToken))
                return BadRequest(ApiResponse<object>.Fail(
                    "MISSING_TOKEN", "Confirmation token is required."));

            try
            {
                var count = await _libraryService.NuclearResetAsync(request.ConfirmationToken, ct);
                return Ok(ApiResponse<object>.Ok(new { deleted = count }));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ApiResponse<object>.Fail("INVALID_TOKEN", ex.Message));
            }
        }

        [HttpPost("clear-scanner-data")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ClearScannerData(CancellationToken ct)
        {
            var count = await _libraryService.ClearScannerDataAsync(ct);
            return Ok(ApiResponse<object>.Ok(new { deleted = count }));
        }

        private int GetUserId() =>
            int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        /// <summary>
        /// Returns true when the given MetadataJson contains a fileScanner entry with at least
        /// one non-null file path.  Mirrors the same helper in MediaController.
        /// </summary>
        private static double? ExtractResolvedRating(string? metadataJson)
        {
            if (string.IsNullOrEmpty(metadataJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                if (doc.RootElement.TryGetProperty("_resolved", out var r) &&
                    r.TryGetProperty("rating", out var ratingEl) &&
                    ratingEl.ValueKind == JsonValueKind.Number)
                    return ratingEl.GetDouble();
            }
            catch { /* ignore */ }
            return null;
        }

        private static bool HasFileScannerData(string? metadataJson)
        {
            if (string.IsNullOrEmpty(metadataJson)) return false;
            if (!metadataJson.Contains("\"fileScanner\"", StringComparison.Ordinal)) return false;
            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                if (!doc.RootElement.TryGetProperty("fileScanner", out var fs)) return false;
                if (fs.TryGetProperty("filePaths", out var fp) &&
                    fp.ValueKind == JsonValueKind.Array &&
                    fp.GetArrayLength() > 0)
                    return true;
                if (fs.TryGetProperty("filePath", out var f) &&
                    f.ValueKind != JsonValueKind.Null &&
                    !string.IsNullOrEmpty(f.GetString()))
                    return true;
                return false;
            }
            catch { return false; }
        }

        private static LibraryEntryDto ToDto(
            UserLibrary e,
            List<string?>? directChildrenMeta = null,
            List<string?>? grandchildrenMeta = null)
        {
            MediaItemDto? mediaDto = null;
            if (e.MediaItem != null)
            {
                // Compute physical-file indicators using the same leaf-level logic as MediaController.
                bool hasOwnFile = HasFileScannerData(e.MediaItem.MetadataJson);
                bool childrenHaveFile;
                bool childrenMissFile;

                if (grandchildrenMeta?.Count > 0)
                {
                    childrenHaveFile = grandchildrenMeta.Any(HasFileScannerData);
                    childrenMissFile = grandchildrenMeta.Any(j => !HasFileScannerData(j));
                }
                else if (directChildrenMeta?.Count > 0)
                {
                    childrenHaveFile = directChildrenMeta.Any(HasFileScannerData);
                    childrenMissFile = directChildrenMeta.Any(j => !HasFileScannerData(j));
                }
                else
                {
                    childrenHaveFile = false;
                    childrenMissFile = false;
                }

                bool hasPhysicalFile = hasOwnFile || childrenHaveFile;
                bool hasMetadataOnly = !hasPhysicalFile || childrenMissFile;

                mediaDto = new MediaItemDto(
                    e.MediaItem.Id, e.MediaItem.MediaTypeId,
                    e.MediaItem.MediaType?.DisplayName ?? string.Empty,
                    e.MediaItem.ParentId, e.MediaItem.Name, e.MediaItem.Year,
                    e.MediaItem.Overview, e.MediaItem.PosterUrl, e.MediaItem.RuntimeMinutes,
                    e.MediaItem.HierarchyLevel, e.MediaItem.Number,
                    e.MediaItem.CreatedAt, e.MediaItem.UpdatedAt,
                    e.MediaItem.ExternalIds.Select(x => new ExternalIdDto(x.Source, x.ExternalId)).ToList(),
                    HasPhysicalFile: hasPhysicalFile,
                    HasMetadataOnly: hasMetadataOnly,
                    ResolvedMetadata: new ResolvedMetadataDto(
                        Title: null, Overview: null, Year: null,
                        PosterUrl: null, BackdropUrl: null, RuntimeMinutes: null,
                        Rating: ExtractResolvedRating(e.MediaItem.MetadataJson),
                        Genres: null, Cast: null, Directors: null, Tags: null),
                    IsCollectionContainer: e.MediaItem.HierarchyLevel == 0
                        && (e.MediaItem.MediaType?.Name ?? string.Empty).Equals("movies", StringComparison.OrdinalIgnoreCase)
                        && directChildrenMeta?.Count > 0,
                    IsStub: e.MediaItem.IsStub);
            }

            var userRatingSource = e.UserRating.HasValue
                ? MediaController.ExtractUserRatingSource(e.MediaItem?.MetadataJson, e.UserRating.Value)
                : null;

            return new LibraryEntryDto(
                e.Id, e.UserId, mediaDto!, e.Status.ToString(),
                e.UserRating, userRatingSource, e.Notes, e.AddedAt, e.UpdatedAt,
                e.StartedAt, e.CompletedAt);
        }
    }
}
