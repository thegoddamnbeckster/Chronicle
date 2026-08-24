using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.API.Helpers;
using Chronicle.Core.Exceptions;
using Chronicle.Data;
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
        private readonly ChronicleDbContext _context;

        public ScrobbleController(IScrobbleService scrobbleService, ChronicleDbContext context)
        {
            _scrobbleService = scrobbleService;
            _context         = context;
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
            [FromQuery] int? mediaItemId = null,
            CancellationToken ct = default)
        {
            var userId = GetUserId();
            var events = await _scrobbleService.GetHistoryAsync(userId, page, perPage, mediaItemId);

            // Ancestor context (e.g. "Show, Season" for an episode) — a scanned TV episode's
            // own name is often a generic code like "S28E11", meaningless without knowing
            // which show/season it's from.
            var ancestorsByItem = await AncestorHelper.BuildAncestorsBatchAsync(
                _context, events.Select(e => e.MediaItemId), ct);

            var dtos = events.Select(e =>
            {
                ancestorsByItem.TryGetValue(e.MediaItemId, out var ancestors);
                return new HistoryItemDto(
                    e.Id,
                    e.MediaItemId,
                    e.MediaItem?.Name ?? string.Empty,
                    e.ProgressPercent,
                    e.Timestamp,
                    e.MarkedAsWatched,
                    e.DeviceName,
                    Ancestors: ancestors is { Count: > 0 } ? ancestors : null
                );
            }).ToList();

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

        /// <summary>
        /// The cross-device "resume where I left off" check — a scrobble client calls
        /// this on playback start, before it has any local resume bookmark of its own
        /// (e.g. the very first time this device plays this item), to pick up wherever
        /// a *different* device last left off. POST (not GET) since ExternalIds is a
        /// dictionary body, same as scrobbling itself. success:true with data:null (not
        /// 404) when the item can't be resolved or has nothing to resume — this is a
        /// routine "no" a client checks on every playback start, not an error.
        /// </summary>
        [HttpPost("resume")]
        public async Task<IActionResult> GetResumeState([FromBody] ResumeLookupRequestDto request)
        {
            var userId = GetUserId();
            var state = await _scrobbleService.GetResumeStateAsync(userId, new ResumeLookupRequest(
                request.MediaItemId,
                request.ExternalIds,
                request.Title,
                request.Year,
                request.MediaType
            ), HttpContext.RequestAborted);

            if (state is null)
                return Ok(ApiResponse<ResumeStateDto>.Ok(null!));

            return Ok(ApiResponse<ResumeStateDto>.Ok(new ResumeStateDto(
                state.MediaItemId, state.ResumePositionPercent, state.ResumeUpdatedAt)));
        }

        private int GetUserId() =>
            int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }
}
