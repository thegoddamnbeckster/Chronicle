using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QRCoder;

namespace Chronicle.API.Controllers;

/// <summary>
/// QR-code device authentication (similar to OAuth 2.0 Device Authorization Grant).
///
/// Unauthenticated endpoints (called by the device / Kodi addon):
///   POST   /api/v1/auth/device             — initiate, get code + QR URL
///   GET    /api/v1/auth/device/{code}/poll — poll for approval status
///   GET    /api/v1/auth/device/{code}/qr   — fetch QR code PNG
///
/// Authenticated endpoints (called from the user's browser):
///   GET    /api/v1/auth/device/{code}         — get info for approval page
///   POST   /api/v1/auth/device/{code}/approve — grant access
///   POST   /api/v1/auth/device/{code}/deny    — deny access
/// </summary>
[ApiController]
[Route("api/v1/auth/device")]
public class DeviceAuthController : ControllerBase
{
    private readonly IDeviceAuthService _deviceAuth;

    public DeviceAuthController(IDeviceAuthService deviceAuth)
    {
        _deviceAuth = deviceAuth;
    }

    // ── Device-side (no auth required) ────────────────────────────────────────

    /// <summary>Start a device-auth session. Called by the Kodi addon.</summary>
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Initiate([FromBody] InitiateDeviceAuthRequestDto? request)
    {
        var baseUrl = GetBaseUrl();
        var result  = await _deviceAuth.InitiateAsync(request?.DeviceName, baseUrl);

        var dto = new InitiateDeviceAuthResponseDto(
            result.Code,
            result.DisplayCode,
            result.VerificationUrl,
            $"{baseUrl.TrimEnd('/')}/api/v1/auth/device/{result.Code}/qr",
            result.ExpiresAt,
            result.ExpiresInSeconds);

        return Ok(ApiResponse<InitiateDeviceAuthResponseDto>.Ok(dto));
    }

    /// <summary>Poll for approval. Called every ~5 seconds by the Kodi addon.</summary>
    [HttpGet("{code}/poll")]
    [AllowAnonymous]
    public async Task<IActionResult> Poll(string code)
    {
        var result = await _deviceAuth.PollAsync(code);
        var dto    = new PollDeviceAuthResponseDto(result.Status, result.ApiKey);
        return Ok(ApiResponse<PollDeviceAuthResponseDto>.Ok(dto));
    }

    /// <summary>Return a QR code PNG for the verification URL. Called by the Kodi addon.</summary>
    [HttpGet("{code}/qr")]
    [AllowAnonymous]
    public async Task<IActionResult> GetQr(string code)
    {
        var url = await _deviceAuth.GetVerificationUrlAsync(code, GetBaseUrl());
        if (url is null)
            return NotFound();

        using var generator = new QRCodeGenerator();
        var data   = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var qrCode = new PngByteQRCode(data);
        var png    = qrCode.GetGraphic(10);   // 10 px per module → ~310px for a typical QR

        return File(png, "image/png");
    }

    // ── Browser-side (JWT auth required) ──────────────────────────────────────

    /// <summary>Return info about the pending code for the approval page.</summary>
    [HttpGet("{code}")]
    [Authorize]
    public async Task<IActionResult> GetInfo(string code)
    {
        var info = await _deviceAuth.GetInfoAsync(code);
        if (info is null)
            return NotFound(ApiResponse<DeviceAuthInfoDto>.Fail("CODE_NOT_FOUND", "Code not found."));

        var dto = new DeviceAuthInfoDto(info.DisplayCode, info.DeviceName, info.Status, info.ExpiresAt);
        return Ok(ApiResponse<DeviceAuthInfoDto>.Ok(dto));
    }

    /// <summary>Approve the device. Called when the user clicks "Allow".</summary>
    [HttpPost("{code}/approve")]
    [Authorize]
    public async Task<IActionResult> Approve(string code)
    {
        try
        {
            await _deviceAuth.ApproveAsync(GetUserId(), code);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (DeviceAuthCodeNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail("CODE_NOT_FOUND", ex.Message));
        }
        catch (DeviceAuthCodeExpiredException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("CODE_EXPIRED", ex.Message));
        }
        catch (DeviceAuthCodeAlreadyUsedException ex)
        {
            return Conflict(ApiResponse<object>.Fail("CODE_USED", ex.Message));
        }
    }

    /// <summary>Deny the device. Called when the user clicks "Deny".</summary>
    [HttpPost("{code}/deny")]
    [Authorize]
    public async Task<IActionResult> Deny(string code)
    {
        try
        {
            await _deviceAuth.DenyAsync(GetUserId(), code);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (DeviceAuthCodeNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail("CODE_NOT_FOUND", ex.Message));
        }
        catch (DeviceAuthCodeExpiredException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("CODE_EXPIRED", ex.Message));
        }
        catch (DeviceAuthCodeAlreadyUsedException ex)
        {
            return Conflict(ApiResponse<object>.Fail("CODE_USED", ex.Message));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int GetUserId() =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Build the Chronicle base URL from the current request so the QR code
    /// always points to the right host (LAN IP, domain, or localhost).
    /// </summary>
    private string GetBaseUrl() =>
        $"{Request.Scheme}://{Request.Host}";
}
