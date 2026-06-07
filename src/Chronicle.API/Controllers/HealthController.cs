using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers
{
    [ApiController]
    [Route("api")]
    public class HealthController : ControllerBase
    {
        /// <summary>
        /// Health check endpoint. Responds to both /health (bare, used by orchestration tools
        /// and the Claude Agent SDK) and /api/health (canonical API path).
        /// </summary>
        [HttpGet("health")]         // → /api/health
        [HttpGet("/health")]        // → /health  (leading slash = root-absolute)
        public IActionResult Health() => Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }
}
