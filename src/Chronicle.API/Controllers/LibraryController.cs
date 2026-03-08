using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers
{
    [ApiController]
    [Route("api/v1/library")]
    [Authorize]
    public class LibraryController : ControllerBase
    {
        private readonly ILibraryService _libraryService;

        public LibraryController(ILibraryService libraryService)
        {
            _libraryService = libraryService;
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
            [FromQuery] int perPage = 20)
        {
            var userId = GetUserId();
            LibraryStatus? parsedStatus = null;

            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<LibraryStatus>(status, out var s))
                    return BadRequest(ApiResponse<List<LibraryEntryDto>>.Fail("INVALID_STATUS", $"Unknown status '{status}'."));
                parsedStatus = s;
            }

            var entries = await _libraryService.GetForUserAsync(userId, parsedStatus, page, perPage);
            return Ok(ApiResponse<List<LibraryEntryDto>>.Ok(
                entries.Select(ToDto).ToList(),
                new PaginationInfo(page, perPage, null)));
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

        private int GetUserId() =>
            int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        private static LibraryEntryDto ToDto(UserLibrary e)
        {
            var mediaDto = e.MediaItem == null ? null! : new MediaItemDto(
                e.MediaItem.Id, e.MediaItem.MediaTypeId,
                e.MediaItem.MediaType?.DisplayName ?? string.Empty,
                e.MediaItem.ParentId, e.MediaItem.Name, e.MediaItem.Year,
                e.MediaItem.Overview, e.MediaItem.PosterUrl, e.MediaItem.RuntimeMinutes,
                e.MediaItem.HierarchyLevel, e.MediaItem.Number,
                e.MediaItem.CreatedAt, e.MediaItem.UpdatedAt,
                e.MediaItem.ExternalIds.Select(x => new ExternalIdDto(x.Source, x.ExternalId)).ToList());

            return new LibraryEntryDto(
                e.Id, e.UserId, mediaDto, e.Status.ToString(),
                e.UserRating, e.Notes, e.AddedAt, e.UpdatedAt,
                e.StartedAt, e.CompletedAt);
        }
    }
}
