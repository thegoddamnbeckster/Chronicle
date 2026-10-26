using System.Collections.Concurrent;
using System.Security.Claims;
using Chronicle.API.Authentication;
using Chronicle.Services;

namespace Chronicle.API;

/// <summary>
/// Keeps a log of the Kodi installs that call Chronicle: every authenticated scraper request made
/// with an API key stamps that key's KodiDevice row (created on first contact), so the list shows
/// each install, where it connects from and when it last did. Nothing here is used to reach a
/// device -- Chronicle never calls Kodi. At most one write per install per minute.
/// </summary>
public sealed class KodiDeviceTouchMiddleware(RequestDelegate next, ILogger<KodiDeviceTouchMiddleware> log)
{
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<int, DateTime> _lastTouched = new();

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);
        try
        {
            await TouchAsync(context);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not note a Kodi install's contact");   // a log entry must never fail a request
        }
    }

    private async Task TouchAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/scraper")) return;
        if (context.Response.StatusCode is 401 or 403) return;
        var tokenClaim = context.User.FindFirstValue(ApiKeyAuthenticationHandler.ApiTokenIdClaimType);
        var userClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (tokenClaim is null || userClaim is null) return;
        var tokenId = int.Parse(tokenClaim);

        var now = DateTime.UtcNow;
        if (_lastTouched.TryGetValue(tokenId, out var last) && now - last < MinimumGap) return;
        _lastTouched[tokenId] = now;

        var devices = context.RequestServices.GetRequiredService<IKodiDeviceService>();
        var host = Describe(context);
        if (await devices.TouchAsync(int.Parse(userClaim), tokenId, host, context.RequestAborted))
            log.LogInformation("A new Kodi install made contact: API key {TokenId} from {Host}", tokenId, host);
    }

    /// <summary>The address a request came from, as plain dotted-quad for an IPv4 peer (a dual-stack
    /// listener wraps one as ::ffff:a.b.c.d). UseForwardedHeaders makes this the real client behind a proxy.</summary>
    public static string? Describe(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        return ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4().ToString() : ip?.ToString();
    }
}
