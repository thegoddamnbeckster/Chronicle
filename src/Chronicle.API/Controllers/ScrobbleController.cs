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
            if (request.MediaItemId is null && string.IsNullOrWhiteSpace(request.Title))
                return BadRequest(ApiResponse<ScrobbleResponseDto>.Fail(
                    "MEDIA_ITEM_REQUIRED", "Supply either mediaItemId or title (+ optional externalIds/year)."));

            var userId = GetUserId();
            try
            {
                var result = await _scrobbleService.ScrobbleAsync(userId, new ScrobbleRequest(
                    request.MediaItemId,
                    request.ProgressPercent,
                    request.Timestamp,
                    request.DeviceName,
                    request.ExternalIds,
                    request.Title,
                    request.Year,
                    request.MediaType
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
            catch (ArgumentException ex)
            {
                return BadRequest(ApiResponse<ScrobbleResponseDto>.Fail("MEDIA_ITEM_REQUIRED", ex.Message));
            }
        }

        [HttpGet("history")]
        public async Task<IActionResult> GetHistory(
            [FromQuery] int page = 1,
            [FromQuery] int perPage = 20,
            [FromQuery] int? mediaItemId = null)
        {
            var userId = GetUserId();
            var events = await _scrobbleService.GetHistoryAsync(userId, page, perPage, mediaItemId);

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

        /// <summary>
        /// Watch-count summary for one item — used by sync clients (e.g. the Kodi addon)
        /// to reconcile play count/last-played against another system's own count
        /// without paging through the full scrobble history.
        /// </summary>
        [HttpGet("summary/{mediaItemId:int}")]
        public async Task<IActionResult> GetWatchSummary(int mediaItemId)
        {
            var userId = GetUserId();
            var (lastWatchedAt, watchedCount) = await _scrobbleService.GetWatchSummaryAsync(userId, mediaItemId);
            return Ok(ApiResponse<WatchSummaryDto>.Ok(new WatchSummaryDto(lastWatchedAt, watchedCount)));
        }

        private int GetUserId() =>
            int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }
}
