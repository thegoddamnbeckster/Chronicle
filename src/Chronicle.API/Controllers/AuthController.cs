using System.Security.Claims;
using Chronicle.API.Authentication;
using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Services;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers
{
    [ApiController]
    [Route("api/v1/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IUserService _userService;
        private readonly ISessionStore _sessions;
        private readonly ILoginThrottle _throttle;
        private readonly AuthAuditLog _audit;

        public AuthController(IUserService userService, ISessionStore sessions,
            ILoginThrottle throttle, AuthAuditLog audit)
        {
            _userService = userService;
            _sessions = sessions;
            _throttle = throttle;
            _audit = audit;
        }

        private string? CallerAddress => HttpContext.Connection.RemoteIpAddress?.ToString();

        /// <summary>429 with a Retry-After header and the API's usual error envelope.</summary>
        private IActionResult TooManyAttempts(TimeSpan retryAfter, string what)
        {
            var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            Response.Headers.RetryAfter = seconds.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, ApiResponse<AuthResponse>.Fail(
                "TOO_MANY_ATTEMPTS",
                $"Too many {what}. Try again in {(seconds < 90 ? $"{seconds} seconds" : $"{(int)Math.Ceiling(seconds / 60.0)} minutes")}."));
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            var allowed = _throttle.CheckRegistration(CallerAddress);
            if (!allowed.Allowed)
            {
                _audit.RegisterThrottled(HttpContext, allowed.RetryAfter);
                return TooManyAttempts(allowed.RetryAfter, "new accounts from this address");
            }
            // Counted whether or not it succeeds: a duplicate-username probe is also an attempt.
            _throttle.RecordRegistration(CallerAddress);

            try
            {
                var user = await _userService.RegisterAsync(request.Username, request.Password, request.Email);
                var (key, session) = _sessions.Create(user.Id, AuthAudit.Agent(HttpContext), AuthAudit.Ip(HttpContext));
                SessionCookie.Set(Response, key);
                _audit.Registered(HttpContext, user.Username, user.Id, session.SessionId);
                var dto = await ToDtoAsync(user);
                return Ok(ApiResponse<AuthResponse>.Ok(new AuthResponse(key, dto)));
            }
            catch (DuplicateUsernameException ex)
            {
                _audit.RegisterFailed(HttpContext, request.Username, "username already taken");
                return Conflict(ApiResponse<AuthResponse>.Fail("USERNAME_TAKEN", ex.Message));
            }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            // Checked BEFORE the password, so a throttled caller learns nothing about whether a
            // guess was right.
            var allowed = _throttle.CheckLogin(CallerAddress, request.Username);
            if (!allowed.Allowed)
            {
                _audit.LoginThrottled(HttpContext, request.Username, allowed.RetryAfter);
                return TooManyAttempts(allowed.RetryAfter, "failed sign-in attempts");
            }

            try
            {
                var user = await _userService.AuthenticateAsync(request.Username, request.Password);
                _throttle.RecordLoginSuccess(CallerAddress, request.Username);
                var (key, session) = _sessions.Create(user.Id, AuthAudit.Agent(HttpContext), AuthAudit.Ip(HttpContext));
                SessionCookie.Set(Response, key);
                _audit.LoginSucceeded(HttpContext, user.Username, user.Id, session.SessionId);
                var dto = await ToDtoAsync(user);
                return Ok(ApiResponse<AuthResponse>.Ok(new AuthResponse(key, dto)));
            }
            catch (InvalidCredentialsException)
            {
                // One message for unknown user, wrong password and deactivated account, so the
                // response never reveals which usernames exist; the log keeps the attempt.
                _throttle.RecordLoginFailure(CallerAddress, request.Username);
                _audit.LoginFailed(HttpContext, request.Username, "invalid username or password, or account inactive");
                return Unauthorized(ApiResponse<AuthResponse>.Fail("INVALID_CREDENTIALS", "Invalid username or password."));
            }
        }

        // ── Session management ────────────────────────────────────────────────
        // Session-scheme only: an API key (a scrobbler or Kodi device) may not manage browser
        // sessions, whoever owns it.

        /// <summary>Ends the calling session.</summary>
        [HttpPost("logout")]
        [Authorize(Policy = AuthPolicies.SessionOnly)]
        public IActionResult Logout()
        {
            var sessionId = CurrentSessionId;
            if (sessionId is not null && _sessions.Revoke(sessionId.Value))
                _audit.LoggedOut(HttpContext, CurrentUserId, sessionId.Value);
            SessionCookie.Clear(Response);
            return Ok(ApiResponse<object>.Ok(new { loggedOut = true }));
        }

        /// <summary>Ends every one of the caller's sessions, including this one.</summary>
        [HttpPost("logout-all")]
        [Authorize(Policy = AuthPolicies.SessionOnly)]
        public IActionResult LogoutAll()
        {
            var count = _sessions.RevokeAllForUser(CurrentUserId);
            SessionCookie.Clear(Response);
            _audit.SessionsRevoked(HttpContext, CurrentUserId, CurrentUserId, count, "user signed out everywhere");
            return Ok(ApiResponse<object>.Ok(new { revoked = count }));
        }

        /// <summary>The caller's active sessions, most recently used first.</summary>
        [HttpGet("sessions")]
        [Authorize(Policy = AuthPolicies.SessionOnly)]
        public IActionResult ListSessions()
        {
            var current = CurrentSessionId;
            var list = _sessions.ListForUser(CurrentUserId)
                .Select(s => new SessionDto(s.SessionId, s.CreatedAt, s.LastSeenAt, s.AbsoluteExpiresAt,
                    s.UserAgent, s.RemoteIp, s.SessionId == current))
                .ToList();
            return Ok(ApiResponse<List<SessionDto>>.Ok(list));
        }

        /// <summary>Ends one of the caller's own sessions. Another user's session id is a 404,
        /// so ids cannot be probed.</summary>
        [HttpDelete("sessions/{id:guid}")]
        [Authorize(Policy = AuthPolicies.SessionOnly)]
        public IActionResult RevokeSession(Guid id)
        {
            var target = _sessions.Get(id);
            if (target is null || target.UserId != CurrentUserId)
                return NotFound(ApiResponse<object>.Fail("SESSION_NOT_FOUND", "Session not found."));

            _sessions.Revoke(id);
            _audit.SessionsRevoked(HttpContext, CurrentUserId, CurrentUserId, 1, "user ended a session");
            return Ok(ApiResponse<object>.Ok(new { revoked = 1 }));
        }

        private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        private Guid? CurrentSessionId =>
            Guid.TryParse(User.FindFirstValue(SessionAuthenticationHandler.SessionIdClaimType), out var g) ? g : null;

        private async Task<UserDto> ToDtoAsync(Chronicle.Core.Models.User u)
        {
            var prefs = await _userService.GetPreferencesAsync(u.Id);
            return new(u.Id, u.Username, u.Email, u.DisplayName, u.IsAdmin,
                prefs.ShowDiagnostics ?? u.IsAdmin,
                prefs.ShowNowPlayingBanner ?? true,
                prefs.ShowAllCredits ?? false);
        }
    }
}
