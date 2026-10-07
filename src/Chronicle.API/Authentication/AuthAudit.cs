using System.Collections.Concurrent;
using System.Text;

namespace Chronicle.API.Authentication;

/// <summary>Text helpers shared by the audit log and anything else that records caller-supplied data.</summary>
public static class AuthAudit
{
    /// <summary>The caller's address as the app sees it. Forwarded-for headers may be trusted from
    /// any source (see <c>Security:TrustedProxies</c>), so a client can claim any address; when the
    /// header middleware rewrote it, the real TCP peer is logged alongside so a spoofed value is
    /// visible in the log.</summary>
    public static string? Ip(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        var peer = ctx.Request.Headers["X-Original-For"].ToString();
        return string.IsNullOrEmpty(peer) ? ip : $"{ip} (forwarded; connection from {peer})";
    }

    public static string? Agent(HttpContext ctx) =>
        ctx.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? Clean(ua, 200) : null;

    /// <summary>Text typed by an unauthenticated caller, made safe to log: control characters
    /// (log-line forging) removed and length capped. A mistyped password in the username box is
    /// still logged as typed, which is the usual price of recording who tried to sign in.</summary>
    public static string? Clean(string? value, int max = 50)
    {
        if (value is null) return null;
        var sb = new StringBuilder(Math.Min(value.Length, max));
        foreach (var c in value)
        {
            if (sb.Length >= max) { sb.Append((char)0x2026); break; }
            sb.Append(char.IsControl(c) ? '?' : c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// One place for connection/authentication audit logging, so every attempt to get in -- by
/// password, session key or API key, successful or not -- is recorded the same way: who (as
/// far as the caller claims), from where, with what, and the outcome. Keys and passwords are
/// never logged; a rejected key is identified only by a short hash tag. Registered in DI so a
/// test host can swap in its own logger.
/// </summary>
public sealed class AuthAuditLog
{
    private readonly Func<Serilog.ILogger> _logger;

    // A browser tab with a dead key polls every few seconds. Without this, one stale tab would
    // write a warning per poll. Keyed on (address, scheme, reason, key tag) so a genuinely
    // different attacker or key is never suppressed.
    private readonly ConcurrentDictionary<string, DateTime> _lastLogged = new();
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(60);

    /// <param name="logger">Resolved on each use so it follows Serilog reconfiguration.</param>
    public AuthAuditLog(Func<Serilog.ILogger> logger) => _logger = logger;

    public static AuthAuditLog CreateDefault() =>
        new(() => Serilog.Log.ForContext("SourceContext", "Chronicle.Auth"));

    private Serilog.ILogger Log => _logger();

    public void LoginSucceeded(HttpContext ctx, string username, int userId, Guid sessionId) =>
        Log.Information("AUTH login OK: user {Username} (id {UserId}) from {RemoteIp}, session {SessionId}, agent {UserAgent}",
            username, userId, AuthAudit.Ip(ctx), sessionId, AuthAudit.Agent(ctx));

    public void LoginFailed(HttpContext ctx, string? username, string reason) =>
        Log.Warning("AUTH login FAILED: user {Username} from {RemoteIp}: {Reason}, agent {UserAgent}",
            AuthAudit.Clean(username), AuthAudit.Ip(ctx), reason, AuthAudit.Agent(ctx));

    public void LoginThrottled(HttpContext ctx, string? username, TimeSpan retryAfter) =>
        Log.Warning("AUTH login THROTTLED: user {Username} from {RemoteIp}: too many failed attempts, retry in {RetryAfterSeconds}s, agent {UserAgent}",
            AuthAudit.Clean(username), AuthAudit.Ip(ctx), (int)Math.Ceiling(retryAfter.TotalSeconds), AuthAudit.Agent(ctx));

    public void Registered(HttpContext ctx, string username, int userId, Guid sessionId) =>
        Log.Information("AUTH register OK: new user {Username} (id {UserId}) from {RemoteIp}, session {SessionId}, agent {UserAgent}",
            username, userId, AuthAudit.Ip(ctx), sessionId, AuthAudit.Agent(ctx));

    public void RegisterFailed(HttpContext ctx, string? username, string reason) =>
        Log.Warning("AUTH register FAILED: user {Username} from {RemoteIp}: {Reason}, agent {UserAgent}",
            AuthAudit.Clean(username), AuthAudit.Ip(ctx), reason, AuthAudit.Agent(ctx));

    public void RegisterThrottled(HttpContext ctx, TimeSpan retryAfter) =>
        Log.Warning("AUTH register THROTTLED: from {RemoteIp}: too many new accounts, retry in {RetryAfterSeconds}s, agent {UserAgent}",
            AuthAudit.Ip(ctx), (int)Math.Ceiling(retryAfter.TotalSeconds), AuthAudit.Agent(ctx));

    public void LoggedOut(HttpContext ctx, int userId, Guid sessionId) =>
        Log.Information("AUTH logout: user id {UserId} from {RemoteIp}, session {SessionId}", userId, AuthAudit.Ip(ctx), sessionId);

    public void SessionsRevoked(HttpContext ctx, int actingUserId, int targetUserId, int count, string why) =>
        Log.Information("AUTH sessions revoked: {Count} session(s) of user id {TargetUserId} by user id {ActingUserId} from {RemoteIp}: {Why}",
            count, targetUserId, actingUserId, AuthAudit.Ip(ctx), why);

    public void ScopeDenied(HttpContext ctx, int? userId, string scope, string method, string? path) =>
        Log.Warning("AUTH api-key scope denied: key with scope {Scope} (user id {UserId}) from {RemoteIp} may not {Method} {Path}, agent {UserAgent}",
            scope, userId, AuthAudit.Ip(ctx), method, AuthAudit.Clean(path, 200), AuthAudit.Agent(ctx));

    public void DevicePairing(HttpContext ctx, string what, string? code = null, string? deviceName = null) =>
        Log.Information("AUTH device-pairing {What}: code {Code}, device {DeviceName} from {RemoteIp}, agent {UserAgent}",
            what, code, AuthAudit.Clean(deviceName, 80), AuthAudit.Ip(ctx), AuthAudit.Agent(ctx));

    public void DevicePairingThrottled(HttpContext ctx, string what, TimeSpan retryAfter) =>
        Log.Warning("AUTH device-pairing {What} THROTTLED: from {RemoteIp}, retry in {RetryAfterSeconds}s, agent {UserAgent}",
            what, AuthAudit.Ip(ctx), (int)Math.Ceiling(retryAfter.TotalSeconds), AuthAudit.Agent(ctx));

    public void RejectedCredential(HttpContext ctx, string scheme, string reason, string? keyTag = null, int? userId = null)
    {
        var ip = AuthAudit.Ip(ctx);
        var throttleKey = $"{ip}|{scheme}|{reason}|{keyTag}";
        var now = DateTime.UtcNow;
        if (_lastLogged.TryGetValue(throttleKey, out var last) && now - last < Quiet) return;
        _lastLogged[throttleKey] = now;

        // Keep the table from growing without bound under a flood of distinct sources.
        if (_lastLogged.Count > 5000)
            foreach (var stale in _lastLogged.Where(kv => now - kv.Value > Quiet).Select(kv => kv.Key).ToList())
                _lastLogged.TryRemove(stale, out _);

        Log.Warning("AUTH rejected {Scheme} credential from {RemoteIp}: {Reason} (key tag {KeyTag}, user id {UserId}) for {Method} {Path}, agent {UserAgent}",
            scheme, ip, reason, keyTag, userId, ctx.Request.Method, AuthAudit.Clean(ctx.Request.Path.Value, 200), AuthAudit.Agent(ctx));
    }
}
