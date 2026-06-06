using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers
{
    [ApiController]
    [Route("api/v1/scrobble")]
    [Authorize]
    public class ScrobbleController : ControllerBase
    {
        private readonly IScrobbleService _scrobbleService;

        public ScrobbleController(IScrobbleService scrobbleService)
        {
            _scrobbleService = scrobbleService;
        }

        [HttpPost]
        public async Task<IActionResult> Scrobble([FromBody] ScrobbleRequestDto request)
        {
            var userId = GetUserId();
            try
            {
                var result = await _scrobbleService.ScrobbleAsync(userId, new ScrobbleRequest(
                    request.MediaItemId,
                    request.ProgressPercent,
                    request.Timestamp,
                    request.DeviceName
                ), HttpContext.RequestAborted);

                return Ok(ApiResponse<ScrobbleResponseDto>.Ok(new ScrobbleResponseDto(
                    result.Event.Id,
                    result.Event.MediaItemId,
                    result.Event.ProgressPercent,
                    result.Event.Timestamp,
                    result.Event.MarkedAsWatched,
                    result.Event.DeviceName
                )));
            }
            catch (MediaNotFoundException ex)
            {
                return NotFound(ApiResponse<ScrobbleResponseDto>.Fail("MEDIA_NOT_FOUND", ex.Message));
            }
        }

        [HttpGet("history")]
        public async Task<IActionResult> GetHistory(
            [FromQuery] int page = 1,
            [FromQuery] int perPage = 20)
        {
            var userId = GetUserId();
            var events = await _scrobbleService.GetHistoryAsync(userId, page, perPage);

            var dtos = events.Select(e => new HistoryItemDto(
                e.Id,
                e.MediaItemId,
                e.MediaItem?.Name ?? string.Empty,
                e.ProgressPercent,
                e.Timestamp,
                e.MarkedAsWatched,
                e.DeviceName
            )).ToList();

            return Ok(ApiResponse<List<HistoryItemDto>>.Ok(dtos, new PaginationInfo(page, perPage, null)));
        }

        private int GetUserId() =>
            int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }
}
