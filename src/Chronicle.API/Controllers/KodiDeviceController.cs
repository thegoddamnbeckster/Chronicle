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
/// Backs Chronicle_Scraper's device self-registration (lib/device_registration.py) and
/// per-item Kodi-internal-id reporting (both addons, fired whenever an ordinary scan already
/// resolves an item's Kodi-side location) -- the two pieces of information NfoPushService needs
/// to push a freshly-changed item's NFO straight to a specific Kodi instance instead of waiting
/// for that instance to notice on its own. Grouped under the same "api/v1/scraper" prefix the
/// addons already call into, even though ScraperController itself is untouched by this feature.
/// </summary>
[ApiController]
[Route("api/v1/scraper")]
[Authorize]
public class KodiDeviceController(
    IKodiDeviceService devices, PortConfig portConfig, INfoRebuildQueueService rebuildQueue)
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

    /// <summary>Resolves the calling API key to ITS OWN registered KodiDevice.Id -- every
    /// rebuild-queue endpoint below is scoped to this, the same way ReportKodiId already is,
    /// so a claim/complete/release always ties back to a specific one of the user's Kodi
    /// instances (five, in this deployment: upstairs, downstairs, storage, vision, office),
    /// never to "whichever device happens to be calling."</summary>
    private async Task<int?> GetKodiDeviceIdAsync(CancellationToken ct)
    {
        var apiTokenId = GetApiTokenId();
        return apiTokenId is null ? null : await devices.GetDeviceIdForApiTokenAsync(apiTokenId.Value, ct);
    }

    public record RegisterKodiDeviceRequest(string Name, string Host, int Port, string? Username, string? Password);

    /// <summary>POST /api/v1/scraper/devices/kodi/register -- upserts the calling device's own
    /// remote-control address. See KodiDevice's own doc for why this is keyed by the calling
    /// API token, not the user.</summary>
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
        // NfoPushService later fires an authenticated-context outbound request at whatever gets
        // registered here on every edit to an item this "device" reported, so an attacker with a
        // leaked API key registering 127.0.0.1:<this port> would otherwise get a standing,
        // repeatable way to hit Chronicle's own API on a schedule they don't control. This closes
        // the most severe case; it does not attempt a general private/public IP-range policy
        // (reverse proxies, Docker networking, and IPv6 all make that a real design decision, not
        // a one-line guard) -- worth a follow-up if broader hardening is wanted.
        if (request.Port == portConfig.Api)
            return BadRequest(ApiResponse<object>.Fail(
                "INVALID_DEVICE", "That port is Chronicle's own -- refusing to register it as a Kodi device."));

        await devices.RegisterAsync(GetUserId(), apiTokenId.Value,
            string.IsNullOrWhiteSpace(request.Name) ? "Kodi" : request.Name.Trim(),
            request.Host.Trim(), request.Port, request.Username, request.Password, ct);

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

    // ── NFO rebuild queue ────────────────────────────────────────────────────
    // See NfoRebuildQueueItem/NfoRebuildQueueService's own docs for why this exists: multiple
    // Kodi instances sharing one library claim batches of work from here instead of each
    // independently re-walking and re-rebuilding the entire shared library on every scan.

    private const int MaxClaimBatchSize = 100;
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);

    /// <summary>ExcludeKinds lets a caller that already determined it can't resolve a kind
    /// locally this run (values matching NfoRebuildQueueItem.Kind, e.g. "movie"/"tvshow"/
    /// "episode") stop being handed more of it -- see ClaimBatchAsync's own doc.</summary>
    public record ClaimRebuildBatchRequest(int? BatchSize, List<string>? ExcludeKinds);

    /// <summary>POST /api/v1/scraper/nfo-rebuild-queue/claim -- claims up to BatchSize (default
    /// 25, capped at 100) pending items for the calling device. Requires an API key with a
    /// registered device (same requirement as report-kodi-id); returns an empty list rather
    /// than an error if the caller has none, since there's simply nothing this "device" could
    /// claim on behalf of.</summary>
    [HttpPost("nfo-rebuild-queue/claim")]
    public async Task<IActionResult> ClaimRebuildBatch([FromBody] ClaimRebuildBatchRequest? request, CancellationToken ct)
    {
        var deviceId = await GetKodiDeviceIdAsync(ct);
        if (deviceId is null)
            return Ok(ApiResponse<NfoRebuildQueueClaimBatchDto>.Ok(new NfoRebuildQueueClaimBatchDto([], 0)));

        var batchSize = Math.Clamp(request?.BatchSize ?? 25, 1, MaxClaimBatchSize);
        var claimed = await rebuildQueue.ClaimBatchAsync(
            deviceId.Value, batchSize, ClaimLease, request?.ExcludeKinds, ct);
        return Ok(ApiResponse<NfoRebuildQueueClaimBatchDto>.Ok(claimed));
    }

    public record RebuildQueueItemRequest(int QueueItemId);

    /// <summary>POST /api/v1/scraper/nfo-rebuild-queue/complete -- reports that the calling
    /// device successfully confirmed this item's NFO. A no-op if this device doesn't currently
    /// hold the claim (e.g. its lease already lapsed and another device picked it up) -- that
    /// other device's own completion is the one that should stick, not a stale race from this
    /// one.</summary>
    [HttpPost("nfo-rebuild-queue/complete")]
    public async Task<IActionResult> CompleteRebuildItem([FromBody] RebuildQueueItemRequest request, CancellationToken ct)
    {
        var deviceId = await GetKodiDeviceIdAsync(ct);
        if (deviceId is null) return Ok(ApiResponse<object>.Ok(new { completed = false }));

        await rebuildQueue.CompleteAsync(request.QueueItemId, deviceId.Value, ct);
        return Ok(ApiResponse<object>.Ok(new { completed = true }));
    }

    /// <summary>POST /api/v1/scraper/nfo-rebuild-queue/release -- gives up a claim immediately
    /// (this device determined it can't process the item, e.g. it doesn't have this file at
    /// all) so another device doesn't have to wait out the full lease before it becomes
    /// claimable again.</summary>
    [HttpPost("nfo-rebuild-queue/release")]
    public async Task<IActionResult> ReleaseRebuildItem([FromBody] RebuildQueueItemRequest request, CancellationToken ct)
    {
        var deviceId = await GetKodiDeviceIdAsync(ct);
        if (deviceId is null) return Ok(ApiResponse<object>.Ok(new { released = false }));

        await rebuildQueue.ReleaseAsync(request.QueueItemId, deviceId.Value, ct);
        return Ok(ApiResponse<object>.Ok(new { released = true }));
    }

    /// <summary>POST /api/v1/scraper/nfo-rebuild-queue/reseed-all -- admin-only "force a full
    /// rebuild": resets every queue row back to pending (including previously-completed ones)
    /// and seeds any qualifying MediaItem that has no row at all yet. The next round of claims
    /// from any device then covers the whole catalog again, not just genuinely-new items.
    /// Mirrors the manual "Rebuild local NFOs from Chronicle" Kodi-side action's own "force
    /// everything" intent, now expressed centrally instead of per-device.</summary>
    [HttpPost("nfo-rebuild-queue/reseed-all")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ReseedRebuildQueue(CancellationToken ct)
    {
        var pending = await rebuildQueue.ReseedAllAsync(ct);
        return Ok(ApiResponse<object>.Ok(new { pending }));
    }

    /// <summary>GET /api/v1/scraper/nfo-rebuild-queue/status -- read-only snapshot for a status
    /// display: overall completed/pending counts plus a per-device breakdown. Any authenticated
    /// user can view this (unlike reseed-all, viewing progress isn't a destructive action); web
    /// UI callers authenticate via JWT same as everywhere else, no API-key/registered-device
    /// requirement the way the claim/complete/release endpoints above have.</summary>
    [HttpGet("nfo-rebuild-queue/status")]
    public async Task<IActionResult> GetRebuildQueueStatus(CancellationToken ct)
    {
        var status = await rebuildQueue.GetStatusAsync(ct);
        return Ok(ApiResponse<NfoRebuildQueueStatusDto>.Ok(status));
    }

    // ── New-content scan signal ──────────────────────────────────────────────
    // See IKodiDeviceService.SignalNewContentAsync/IsScanNeededAsync's own docs. Deliberately
    // pull, not push: unlike NfoPushService's RefreshAsync (which needs each device's own
    // JSON-RPC-over-HTTP address and "Allow remote control via HTTP" turned on), a device polls
    // this itself and, if due, runs VideoLibrary.Scan via its own LOCAL xbmc.executeJSONRPC --
    // Chronicle never calls out to a device for this.
    //
    // Deliberately resolved by API TOKEN directly (GetApiTokenId), NOT via GetKodiDeviceIdAsync/
    // KodiDevice the way the rebuild-queue endpoints above are: a KodiDevice row only exists
    // once "Allow remote control via HTTP" is on and device_registration.py has self-registered
    // (see KodiDevice's own doc), which would have silently made THIS feature depend on that
    // same setting despite not needing it at all -- confirmed live (2026-09-11) as a real bug,
    // caught before release: a vanilla Kodi install (remote control off by default) never got a
    // KodiDevice row, so GetKodiDeviceIdAsync always returned null and this signal never fired.
    // See KodiScanAck's own doc for the storage side of the fix.

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
}
