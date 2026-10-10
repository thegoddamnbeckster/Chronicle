using System.Security.Claims;
using Chronicle.API.Authentication;
using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers
{
    /// <summary>
    /// The user-administration routes that must never be reachable with an API key: ending another user's
    /// sessions and handing out password-reset codes. They live in their own controller on purpose. In
    /// <c>UsersController</c> the class-level <c>[Authorize]</c> (default policy: session OR API key) is MERGED with a
    /// method-level policy, so a "session only" attribute there still let an administrator's full-scope API key
    /// through - found by a test. Here the class carries the only policy, so what it says is what is enforced.
    /// Same URL prefix as UsersController (api/v1/users/{id}/...).
    /// </summary>
    [ApiController]
    [Route("api/v1/users")]
    [Authorize(Policy = AuthPolicies.SessionAdmin)]
    public class UserSecurityController : ControllerBase
    {
        private readonly ISessionStore _sessions;
        private readonly AuthAuditLog _audit;
        private readonly IPasswordResetService _reset;

        public UserSecurityController(ISessionStore sessions, AuthAuditLog audit, IPasswordResetService reset)
        {
            _sessions = sessions;
            _audit = audit;
            _reset = reset;
        }

        private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // ── Admin: hand someone a password-reset code ─────────────────────────

        /// <summary>
        /// Creates a one-time reset code for a user who cannot use "forgot password" (no email set up, or none on file).
        /// Shown once. The administrator gives it to the person, who enters it on the reset page. Browser session
        /// required: a leaked API key must not be able to take over accounts.
        /// </summary>
        [HttpPost("{id:int}/reset-token")]
        public async Task<IActionResult> IssueResetToken(int id, CancellationToken ct)
        {
            try
            {
                var issued = await _reset.IssueAsync(id, CurrentUserId, PasswordResetService.DeliveryAdmin, ct);
                _audit.PasswordResetIssued(HttpContext, CurrentUserId, id, PasswordResetService.DeliveryAdmin);
                var baseUrl = $"{Request.Scheme}://{Request.Host}";
                return Ok(ApiResponse<ResetTokenDto>.Ok(new ResetTokenDto(
                    issued.Token, issued.ExpiresAtUtc, _reset.BuildResetUrl(baseUrl, issued.Token), issued.Username)));
            }
            catch (UserNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail("USER_NOT_FOUND", ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ApiResponse<object>.Fail("ACCOUNT_INACTIVE", ex.Message));
            }
        }

        // ── Admin: a user's browser sessions ──────────────────────────────────

        /// <summary>A user's active sessions. Session-scheme only, like the self routes.</summary>
        [HttpGet("{id:int}/sessions")]
        public IActionResult ListUserSessions(int id)
        {
            var list = _sessions.ListForUser(id)
                .Select(s => new SessionDto(s.SessionId, s.CreatedAt, s.LastSeenAt, s.AbsoluteExpiresAt,
                    s.UserAgent, s.RemoteIp, false))
                .ToList();
            return Ok(ApiResponse<List<SessionDto>>.Ok(list));
        }

        /// <summary>Ends all of a user's sessions (including the admin's own, if it is their own id).</summary>
        [HttpDelete("{id:int}/sessions")]
        public IActionResult RevokeUserSessions(int id)
        {
            var ended = _sessions.RevokeAllForUser(id);
            _audit.SessionsRevoked(HttpContext, CurrentUserId, id, ended, "ended by admin");
            return Ok(ApiResponse<object>.Ok(new { revoked = ended }));
        }
    }
}
