using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers
{
    [ApiController]
    [Route("api/v1/stats")]
    [Authorize]
    public class StatsController : ControllerBase
    {
        private readonly IStatsService _statsService;
        private readonly ChronicleDbContext _context;

        public StatsController(IStatsService statsService, ChronicleDbContext context)
        {
            _statsService = statsService;
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> GetStats()
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var stats = await _statsService.GetUserStatsAsync(userId);
            return Ok(ApiResponse<UserStats>.Ok(stats));
        }

        [HttpGet("library")]
        public async Task<IActionResult> GetLibraryStats()
        {
            var byType = await _context.MediaItems
                .Include(m => m.MediaType)
                .GroupBy(m => m.MediaType!.DisplayName)
                .Select(g => new { mediaType = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
                .ToListAsync();

            var total = byType.Sum(x => x.count);

            return Ok(ApiResponse<object>.Ok(new
            {
                totalItems = total,
                byMediaType = byType,
            }));
        }
    }
}
