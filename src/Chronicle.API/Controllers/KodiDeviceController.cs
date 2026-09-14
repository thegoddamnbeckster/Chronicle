using System.Security.Claims;
using Chronicle.API.Authentication;
using Chronicle.API.DTOs;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

// PortConfig lives directly in the Chronicle.API namespace (PortManager.cs), a level up
// from Chronicle.API.Controllers -- needs an explicit using, C# namespaces don't nest.
using Chronicle.API;

namespace Chronicle.API.Controllers;

/// <summary>
/// Backs Chronicle_Scraper's device self-registration (lib/device_registration.py), per-item
/// Kodi-internal-id reporting, the new-content scan signal, and the scan-active heartbeat.
/// Grouped under the same "api/v1/scraper" prefix the addons already call into, even though
/// ScraperController itself is untouched by these.
///
/// NFO writing/rebuilding (server-side and client-side alike) was removed entirely 2026-09-13
/// -- per-user direction, after it was identified as the actual mechanism by which a stale or
/// unexpected local NFO file could block Kodi from ever re-scanning an item. Neither Kodi
/// addon requires a local NFO to work; Chronicle's own API is the source of truth both
/// scrapers read from directly. See git history for the removed nfo-rebuild-queue
/// claim/complete/release/reseed-all/status endpoints if you need the old rationale.
/// </summary>
[ApiController]
[Route("api/v1/scraper")]
[Authorize]
public class KodiDeviceController(IKodiDeviceService devices, PortConfig portConfig)
    : ControllerBase
{
    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Null when the caller authenticated via JWT (the web UI) rather than an API key
    /// -- device registration and id-reporting only ever make sense from a paired Kodi
    /// instance's own API key.</summary>
    private int? GetApiTokenId()
    {
        var claim = User.FindFirstValue(ApiKeyAuthenticationHandler.ApiTokenIdClaimType);
        return claim is null ? null : int.Parse(claim);
    }

    public record RegisterKodiDeviceRequest(string Name, string Host, int Port, string? Username, string? Password);

    /// <summary>POST /api/v1/scraper/devices/kodi/register -- upserts the calling device's own
    /// remote-control address. See KodiDevice's own doc for why this is keyed by the calling
    /// API token, not the user.
    ///
    /// The host actually stored comes from THIS REQUEST's own observed remote address, not
    /// request.Host -- confirmed live (2026-09-11) that a device's own self-reported guess
    /// (device_registration.py's UDP-connect-to-8.8.8.8 trick, picking whichever local
    /// interface that route happens to use) can be wrong on a multi-homed device -- e.g. a VPN
    /// client running alongside the LAN connection -- registering an address Chronicle can
    /// never actually reach, silently and permanently (every future re-registration just
    /// repeats the same wrong guess, since the client-side logic that produced it never
    /// changes). The connection's own remote address is what Chronicle would need to reach that
    /// same device again regardless of what interface the device THINKS it's on, and -- since
    /// UseForwardedHeaders is already configured in Program.cs -- correctly reflects the
    /// original client even if a reverse proxy sits in front. request.Host is now only used as
    /// a presence check (kept for backward compatibility with older addon builds that still
    /// send it) and as a fallback for the rare case the remote address can't be read at all.</summary>
    [HttpPost("devices/kodi/register")]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterKodiDeviceRequest request, CancellationToken ct)
    {
        var apiTokenId = GetApiTokenId();
        if (apiTokenId is null)
            return BadRequest(ApiResponse<object>.Fail(
                "API_KEY_REQUIRED", "Device registration requires an API key, not a JWT."));
        if (string.IsNullOrWhiteSpace(request.Host) || request.Port <= 0)
            return BadRequest(ApiResponse<object>.Fail("INVALID_DEVICE", "host and a positive port are required."));
        // SSRF guard: refuse to register Chronicle's own listening port as a "Kodi device" --
        // the now-removed NfoPushService used to fire an authenticated-context outbound request
        // at whatever got registered here on every edit to an item this "device" reported, so an
        // attacker with a leaked API key registering 127.0.0.1:<this port> would otherwise have
        // gotten a standing, repeatable way to hit Chronicle's own API on a schedule they don't
        // control. That concrete attack is moot now that NfoPushService is gone, but the guard
        // stays as a general safeguard against whatever future feature next pushes to a
        // registered device. Doesn't attempt a general private/public IP-range policy (reverse
        // proxies, Docker networking, and IPv6 all make that a real design decision, not a
        // one-line guard) -- worth a follow-up if broader hardening is wanted.
        if (request.Port == portConfig.Api)
            return BadRequest(ApiResponse<object>.Fail(
                "INVALID_DEVICE", "That port is Chronicle's own -- refusing to register it as a Kodi device."));

        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        // Strip the IPv6-mapped-IPv4 prefix (::ffff:10.0.0.10) a dual-stack listener commonly
        // wraps an actual IPv4 peer in -- matches the plain dotted-quad format device_registration.py
        // itself would have sent, so this doesn't look like a surprising format change downstream
        // (the status page's Host column, etc).
        var host = remoteIp is { IsIPv4MappedToIPv6: true } ? remoteIp.MapToIPv4().ToString() : remoteIp?.ToString();
        if (string.IsNullOrWhiteSpace(host))
            host = request.Host.Trim(); // couldn't read a remote address at all -- fall back rather than fail registration outright

        await devices.RegisterAsync(GetUserId(), apiTokenId.Value,
            string.IsNullOrWhiteSpace(request.Name) ? "Kodi" : request.Name.Trim(),
            host, request.Port, request.Username, request.Password, ct);

        return Ok(ApiResponse<object>.Ok(new { registered = true }));
    }

    public record ReportKodiIdRequest(int MediaItemId, string Kind, int KodiId);

    /// <summary>POST /api/v1/scraper/report-kodi-id -- records this device's own internal id
    /// for one MediaItem. A no-op (not an error) when the caller has no registered device --
    /// there's simply nothing to push to for it regardless of whether the id is recorded.</summary>
    [HttpPost("report-kodi-id")]
    public async Task<IActionResult> ReportKodiId([FromBody] ReportKodiIdRequest request, CancellationToken ct)
    {
        var apiTokenId = GetApiTokenId();
        if (apiTokenId is null)
            return Ok(ApiResponse<object>.Ok(new { recorded = false }));

        if (request.KodiId <= 0 || string.IsNullOrWhiteSpace(request.Kind))
            return BadRequest(ApiResponse<object>.Fail("INVALID_REQUEST", "kind and a positive kodiId are required."));

        await devices.RecordKodiIdAsync(apiTokenId.Value, request.MediaItemId, request.Kind.Trim(), request.KodiId, ct);
        return Ok(ApiResponse<object>.Ok(new { recorded = true }));
    }

    // ── New-content scan signal ──────────────────────────────────────────────
    // See IKodiDeviceService.SignalNewContentAsync/IsScanNeededAsync's own docs. Deliberately
    // pull, not push: a device polls this itself and, if due, runs VideoLibrary.Scan via its
    // own LOCAL xbmc.executeJSONRPC -- Chronicle never calls out to a device for this.
    //
    // Deliberately resolved by API TOKEN directly (GetApiTokenId), NOT by requiring a
    // registered KodiDevice row: a KodiDevice row only exists once "Allow remote control via
    // HTTP" is on and device_registration.py has self-registered (see KodiDevice's own doc),
    // which would have silently made THIS feature depend on that same setting despite not
    // needing it at all -- confirmed live (2026-09-11) as a real bug, caught before release: a
    // vanilla Kodi install (remote control off by default) never got a KodiDevice row, so a
    // device-scoped lookup always returned null and this signal never fired. See KodiScanAck's
    // own doc for the storage side of the fix.

    /// <summary>GET /api/v1/scraper/kodi-scan-signal -- true if the calling device is due to run
    /// its own local VideoLibrary.Scan (new movie/TV content was imported since this device last
    /// acknowledged). False (not an error) for a caller with no API key (a JWT/web caller) --
    /// same "nothing to do for this caller" shape as the rebuild-queue endpoints above, though
    /// unlike those, any API-key caller qualifies here even with no prior registration.</summary>
    [HttpGet("kodi-scan-signal")]
    public async Task<IActionResult> GetScanSignal(CancellationToken ct)
    {
        var apiTokenId = GetApiTokenId();
        if (apiTokenId is null) return Ok(ApiResponse<object>.Ok(new { scanNeeded = false }));

        var scanNeeded = await devices.IsScanNeededAsync(apiTokenId.Value, ct);
        return Ok(ApiResponse<object>.Ok(new { scanNeeded }));
    }

    /// <summary>POST /api/v1/scraper/kodi-scan-signal/ack -- reports that the calling device just
    /// ran its own local VideoLibrary.Scan in response to the signal above. Call this AFTER the
    /// scan actually runs, not before (see AcknowledgeScanAsync's own doc).</summary>
    [HttpPost("kodi-scan-signal/ack")]
    public async Task<IActionResult> AcknowledgeScanSignal(CancellationToken ct)
    {
        var apiTokenId = GetApiTokenId();
        if (apiTokenId is null) return Ok(ApiResponse<object>.Ok(new { acknowledged = false }));

        await devices.AcknowledgeScanAsync(apiTokenId.Value, ct);
        return Ok(ApiResponse<object>.Ok(new { acknowledged = true }));
    }

    /// <summary>POST /api/v1/scraper/scan-active -- heartbeat telling Chronicle "a Kodi device is
    /// actively scanning right now," so NfoGenerationService's own scheduled sweep pauses itself
    /// for the duration -- see IKodiDeviceService.ReportScanActivityAsync/IsScanActiveAsync's own
    /// docs. Call once from the addon's xbmc.Monitor.onScanStarted() hook and again periodically
    /// while a scan is still running (there's no onScanProgress callback); no explicit "finished"
    /// endpoint by design -- the flag just expires a couple of minutes after the last heartbeat.
    /// No API-key/registered-device requirement, same reasoning as kodi-scan-signal above: this
    /// must work on a vanilla install with "Allow remote control via HTTP" left off.</summary>
    [HttpPost("scan-active")]
    public async Task<IActionResult> ReportScanActive(CancellationToken ct)
    {
        await devices.ReportScanActivityAsync(ct);
        return Ok(ApiResponse<object>.Ok(new { acknowledged = true }));
    }
}
