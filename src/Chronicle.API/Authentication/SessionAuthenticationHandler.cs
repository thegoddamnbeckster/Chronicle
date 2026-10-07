using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Chronicle.Data;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chronicle.API.Authentication;

/// <summary>Names of the authorization policies that require a browser session specifically.</summary>
public static class AuthPolicies
{
    /// <summary>A signed-in browser session; an API key is not enough.</summary>
    public const string SessionOnly = "SessionOnly";

    /// <summary>A signed-in browser session belonging to an administrator.</summary>
    public const string SessionAdmin = "SessionAdmin";

    /// <summary>For read-only image routes an &lt;img&gt; tag loads: accepts the session key from the
    /// header OR from the session cookie (a tag cannot send headers), plus an API key.</summary>
    public const string ImageRead = "ImageRead";
}

/// <summary>
/// Authenticates the web UI. The browser sends the server-issued session key as
/// <c>Authorization: Bearer chr_sess_...</c>; this handler checks it against the in-memory
/// <see cref="ISessionStore"/> on every request, then reads the user's CURRENT state from the
/// database so a deactivation, deletion or role change takes effect on the very next request
/// (the claims are built here, not copied from login time).
///
/// Produces the same claim shape as <see cref="ApiKeyAuthenticationHandler"/> so controllers
/// work with either scheme unchanged. Every rejection is logged with the caller's address --
/// see <see cref="AuthAuditLog"/> for the throttling that keeps a stale browser tab from flooding
/// the log.
/// </summary>
public class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Session";

    /// <summary>Claim carrying the calling session's id (never its key).</summary>
    public const string SessionIdClaimType = "chronicle:session_id";

    /// <summary>Sent by the web UI's own polling so it does not count as user activity: an
    /// abandoned tab that keeps polling must still hit the idle timeout.</summary>
    public const string BackgroundHeader = "X-Chronicle-Background";

    private readonly ISessionStore _sessions;
    private readonly IDeactivatedUserCache _deactivated;
    private readonly ChronicleDbContext _db;
    private readonly AuthAuditLog _audit;

    public SessionAuthenticationHandler(
        ISessionStore sessions,
        IDeactivatedUserCache deactivated,
        ChronicleDbContext db,
        AuthAuditLog audit,
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
        _deactivated = deactivated;
        _db = db;
        _audit = audit;
    }

    /// <summary>Where this scheme looks for the key. Returns null when the request carries none
    /// (so another scheme may try). <c>touch</c> says whether the request counts as user activity.</summary>
    protected virtual (string? Key, bool Touch) ReadCredential()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header)) return (null, false);

        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)) return (null, false);

        return (header[bearer.Length..].Trim(), !Request.Headers.ContainsKey(BackgroundHeader));
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var (key, touch) = ReadCredential();
        if (key is null) return AuthenticateResult.NoResult();

        // Not a session key (for example a JWT left in a browser from before this scheme
        // existed). Say so in the log, but let any other scheme try.
        if (!key.StartsWith(SessionStore.KeyPrefix, StringComparison.Ordinal))
        {
            _audit.RejectedCredential(Context, Scheme.Name, "credential is not a session key");
            return AuthenticateResult.NoResult();
        }

        var validation = _sessions.Validate(key, touch);
        if (!validation.IsValid)
        {
            var reason = validation.Rejection switch
            {
                SessionRejection.IdleExpired     => "session expired (idle)",
                SessionRejection.LifetimeExpired => "session expired (maximum lifetime)",
                _                                => "unknown or revoked session key",
            };
            _audit.RejectedCredential(Context, Scheme.Name, reason, SessionStore.LogTag(key));
            return AuthenticateResult.Fail(reason);
        }

        var session = validation.Session!;

        if (_deactivated.IsBlocked(session.UserId))
        {
            _sessions.Revoke(session.SessionId);
            _audit.RejectedCredential(Context, Scheme.Name, "account is deactivated", SessionStore.LogTag(key), session.UserId);
            return AuthenticateResult.Fail("Account is deactivated.");
        }

        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == session.UserId)
            .Select(u => new { u.Username, u.IsAdmin, u.IsActive })
            .FirstOrDefaultAsync(Context.RequestAborted);

        if (user is null || !user.IsActive)
        {
            _sessions.Revoke(session.SessionId);
            _audit.RejectedCredential(Context, Scheme.Name,
                user is null ? "account no longer exists" : "account is deactivated",
                SessionStore.LogTag(key), session.UserId);
            return AuthenticateResult.Fail("Account is deactivated or no longer exists.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User"),
            new(SessionIdClaimType, session.SessionId.ToString()),
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

/// <summary>
/// The same session, presented as the HttpOnly cookie set at login. Exists ONLY so
/// <c>&lt;img src&gt;</c> tags - which cannot send an Authorization header - can load images from
/// routes that used to be anonymous. It is accepted only by the <see cref="AuthPolicies.ImageRead"/>
/// policy on read-only GET routes, never as general authentication, so a cross-site request
/// riding the cookie can read nothing but an image. It never counts as user activity.
/// </summary>
public sealed class SessionCookieAuthenticationHandler : SessionAuthenticationHandler
{
    public new const string SchemeName = "SessionCookie";

    public SessionCookieAuthenticationHandler(
        ISessionStore sessions, IDeactivatedUserCache deactivated, ChronicleDbContext db, AuthAuditLog audit,
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(sessions, deactivated, db, audit, options, logger, encoder) { }

    protected override (string? Key, bool Touch) ReadCredential() =>
        Request.Cookies.TryGetValue(SessionCookie.Name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? (value, false)
            : (null, false);
}

/// <summary>Sets and clears the session cookie that lets image tags authenticate.</summary>
public static class SessionCookie
{
    public const string Name = "chronicle_session";

    public static void Set(HttpResponse response, string key) =>
        response.Cookies.Append(Name, key, Options(response.HttpContext));

    public static void Clear(HttpResponse response) =>
        response.Cookies.Delete(Name, Options(response.HttpContext));

    private static CookieOptions Options(HttpContext ctx) => new()
    {
        HttpOnly = true,                 // script cannot read it, so an XSS bug cannot steal it
        SameSite = SameSiteMode.Strict,  // never sent on cross-site requests
        Secure = ctx.Request.IsHttps,    // honours X-Forwarded-Proto behind a TLS proxy
        Path = "/api/",
        IsEssential = true,              // authentication, not tracking
    };
}
