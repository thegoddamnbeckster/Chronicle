using Chronicle.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers
{
    [ApiController]
    [Route("api")]
    public class HealthController : ControllerBase
    {
        private readonly ChronicleDbContext _db;
        private readonly ILogger<HealthController> _log;

        public HealthController(ChronicleDbContext db, ILogger<HealthController> log)
        {
            _db = db;
            _log = log;
        }

        /// <summary>
        /// Health check endpoint. Responds to both /health (bare, used by orchestration tools
        /// and the Claude Agent SDK) and /api/health (canonical API path).
        /// </summary>
        [HttpGet("health")]         // → /api/health
        [HttpGet("/health")]        // → /health  (leading slash = root-absolute)
        public IActionResult Health() => Ok(new { status = "healthy", timestamp = DateTime.UtcNow });

        /// <summary>
        /// Deep check for monitors: the process answering is not enough, it must also be talking to the
        /// RIGHT database. Fails (503) when the database cannot be queried, migrations are pending, or the
        /// database looks freshly created (no users / media types), which is what an API started with the
        /// wrong environment or working directory ends up on. Names only the database file, never its path.
        /// </summary>
        [HttpGet("health/deep")]
        public async Task<IActionResult> DeepHealth(CancellationToken ct)
        {
            var checks = new Dictionary<string, object>();
            var healthy = true;
            void Fail(string name, string detail) { healthy = false; checks[name] = new { ok = false, detail }; }

            try
            {
                if (!_db.Database.IsRelational())
                {
                    checks["database"] = new { ok = true, detail = "non-relational provider" };
                }
                else
                {
                    var pending = (await _db.Database.GetPendingMigrationsAsync(ct)).ToList();
                    // Existence checks, not counts: this endpoint is anonymous and polled, and a count over the media table is a full scan.
                    var hasUsers = await _db.Users.AnyAsync(ct);
                    var hasMediaTypes = await _db.MediaTypes.AnyAsync(ct);
                    var file = _db.Database.IsSqlite()
                        ? Path.GetFileName(_db.Database.GetDbConnection().DataSource)
                        : _db.Database.GetDbConnection().Database;

                    checks["database"] = new { ok = true, detail = file };
                    if (pending.Count > 0)
                        Fail("migrations", $"{pending.Count} database update(s) not applied yet");
                    else
                        checks["migrations"] = new { ok = true };
                    if (!hasUsers || !hasMediaTypes)
                        Fail("data", $"database '{file}' has no {(hasUsers ? "media types" : "users")}: it looks like a fresh or wrong database");
                    else
                        checks["data"] = new { ok = true };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The reason goes to the log; the anonymous caller only learns that the database failed (messages can carry paths).
                _log.LogError(ex, "HEALTH deep check: the database could not be queried");
                Fail("database", "the database could not be queried");
            }

            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var startedAt = self.StartTime.ToUniversalTime();
            var body = new
            {
                status = healthy ? "healthy" : "unhealthy",
                timestamp = DateTime.UtcNow,
                uptimeSeconds = (long)(DateTime.UtcNow - startedAt).TotalSeconds,
                checks,
            };
            return healthy ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
        }
    }
}
