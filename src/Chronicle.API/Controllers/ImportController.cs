using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Plugins;
using Chronicle.Services.Import;
using Chronicle.Services.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

/// <summary>
/// Manages device/PIN OAuth flows and data imports from external tracking
/// services (Trakt, Simkl, …) via loaded import-provider plugins.
///
/// Auth flow:
///   1. POST ./{pluginId}/auth/start   → get user_code + verification_url
///   2. GET  ./{pluginId}/auth/poll/{pollCode}  → poll until authorized
///   3. GET  ./{pluginId}/auth/status  → verify authentication at any time
///
/// Import:
///   POST ./{pluginId}/history    → import watch history
///   POST ./{pluginId}/ratings    → import ratings
///   POST ./{pluginId}/watchlist  → import watchlist
/// </summary>
[ApiController]
[Route("api/v1/import")]
[Authorize]
public class ImportController : ControllerBase
{
    private readonly IImportService _importService;
    private readonly IPluginService _pluginService;

    public ImportController(IImportService importService, IPluginService pluginService)
    {
        _importService = importService;
        _pluginService = pluginService;
    }

    // ── Provider list ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all currently loaded import providers with their capabilities.
    /// Use this to populate the import management page.
    /// </summary>
    [HttpGet("providers")]
    public IActionResult GetProviders()
    {
        var providers = _importService.GetProviders();
        var dtos = providers.Select(p =>
        {
            var caps = p.GetCapabilities();
            return new ImportProviderDto(
                p.PluginId,
                p.Name,
                p.Version,
                p.Description,
                caps.SupportsHistory,
                caps.SupportsRatings,
                caps.SupportsWatchlist,
                caps.RequiresDeviceAuth);
        }).ToList();

        return Ok(ApiResponse<List<ImportProviderDto>>.Ok(dtos));
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the device/PIN authorization flow for the given import plugin.
    /// Returns the code the user must enter at the provider's website and a
    /// poll code to use with the poll endpoint.
    /// </summary>
    [HttpPost("{pluginId}/auth/start")]
    public async Task<IActionResult> StartAuth(string pluginId, CancellationToken ct)
    {
        try
        {
            var result = await _importService.StartAuthAsync(pluginId, ct);
            return Ok(ApiResponse<StartAuthResponse>.Ok(new StartAuthResponse(
                result.UserCode,
                result.VerificationUrl,
                result.ExpiresInSeconds,
                result.PollingIntervalSeconds,
                result.PollCode)));
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(ApiResponse<StartAuthResponse>.Fail("AUTH_NOT_SUPPORTED", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<StartAuthResponse>.Fail("PROVIDER_NOT_FOUND", ex.Message));
        }
    }

    /// <summary>
    /// Polls for authorization completion.
    /// When the user completes auth at the provider's website this returns
    /// status="authorized" and automatically persists the access token into
    /// the plugin's settings so subsequent imports work immediately.
    /// </summary>
    [HttpGet("{pluginId}/auth/poll/{pollCode}")]
    public async Task<IActionResult> PollAuth(
        string pluginId, string pollCode, CancellationToken ct)
    {
        try
        {
            var result = await _importService.PollAuthAsync(pluginId, pollCode, ct);

            // When authorized, merge and persist the returned tokens into the plugin settings.
            // This reconfigures the live provider so imports work without a restart.
            if (result.Status == DeviceAuthStatus.Authorized && result.NewSettings is { Count: > 0 })
                await MergeAndPersistSettingsAsync(pluginId, result.NewSettings);

            return Ok(ApiResponse<PollAuthResponse>.Ok(
                new PollAuthResponse(result.Status.ToString().ToLowerInvariant(), result.ErrorMessage)));
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(ApiResponse<PollAuthResponse>.Fail("AUTH_NOT_SUPPORTED", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<PollAuthResponse>.Fail("PROVIDER_NOT_FOUND", ex.Message));
        }
    }

    /// <summary>
    /// Returns whether the plugin currently has a valid, non-expired access token.
    /// </summary>
    [HttpGet("{pluginId}/auth/status")]
    public async Task<IActionResult> AuthStatus(string pluginId, CancellationToken ct)
    {
        try
        {
            var authenticated = await _importService.IsAuthenticatedAsync(pluginId, ct);
            return Ok(ApiResponse<AuthStatusResponse>.Ok(new AuthStatusResponse(authenticated)));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<AuthStatusResponse>.Fail("PROVIDER_NOT_FOUND", ex.Message));
        }
    }

    // ── Import ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Imports the authenticated user's watch history from the given service.
    /// Pass ?since=2024-01-01T00:00:00Z to fetch only events after that timestamp.
    /// </summary>
    [HttpPost("{pluginId}/history")]
    public async Task<IActionResult> ImportHistory(
        string pluginId,
        [FromQuery] DateTimeOffset? since,
        CancellationToken ct)
    {
        var userId = GetUserId();
        try
        {
            var result = await _importService.ImportHistoryAsync(pluginId, userId, since, ct);
            return Ok(ApiResponse<ImportResultResponse>.Ok(
                new ImportResultResponse(result.Imported, result.Skipped, result.Errors)));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<ImportResultResponse>.Fail("PROVIDER_NOT_FOUND", ex.Message));
        }
    }

    /// <summary>Imports the authenticated user's ratings from the given service.</summary>
    [HttpPost("{pluginId}/ratings")]
    public async Task<IActionResult> ImportRatings(string pluginId, CancellationToken ct)
    {
        var userId = GetUserId();
        try
        {
            var result = await _importService.ImportRatingsAsync(pluginId, userId, ct);
            return Ok(ApiResponse<ImportResultResponse>.Ok(
                new ImportResultResponse(result.Imported, result.Skipped, result.Errors)));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<ImportResultResponse>.Fail("PROVIDER_NOT_FOUND", ex.Message));
        }
    }

    /// <summary>Imports the authenticated user's watchlist from the given service.</summary>
    [HttpPost("{pluginId}/watchlist")]
    public async Task<IActionResult> ImportWatchlist(string pluginId, CancellationToken ct)
    {
        var userId = GetUserId();
        try
        {
            var result = await _importService.ImportWatchlistAsync(pluginId, userId, ct);
            return Ok(ApiResponse<ImportResultResponse>.Ok(
                new ImportResultResponse(result.Imported, result.Skipped, result.Errors)));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<ImportResultResponse>.Fail("PROVIDER_NOT_FOUND", ex.Message));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int GetUserId() =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Merges the new token key-value pairs into the plugin's existing settings
    /// and persists them so the access token survives restarts and the live
    /// provider instance is reconfigured immediately.
    /// </summary>
    private Task MergeAndPersistSettingsAsync(
        string pluginId,
        IReadOnlyDictionary<string, string> newSettings)
        => _pluginService.MergeSettingsAsync(pluginId, newSettings);
}
