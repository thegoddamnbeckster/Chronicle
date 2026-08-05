using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QRCoder;
using SkiaSharp;

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
    private readonly ILogger<DeviceAuthController> _log;

    public DeviceAuthController(IDeviceAuthService deviceAuth, ILogger<DeviceAuthController> log)
    {
        _deviceAuth = deviceAuth;
        _log        = log;
    }

    // ── Device-side (no auth required) ────────────────────────────────────────

    /// <summary>Start a device-auth session. Called by the Kodi addon.</summary>
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Initiate([FromBody] InitiateDeviceAuthRequestDto? request)
    {
        // Deliberately verbose: device-auth is the one flow where "did the request even
        // arrive" matters more than usual (Kodi add-ons on LAN devices, intermittent
        // network issues that are otherwise invisible from the server side). Kept
        // permanently, not a temporary diagnostic.
        _log.LogInformation(
            "DeviceAuth.Initiate: request received — RemoteIp={RemoteIp} XForwardedFor={XFwd} " +
            "Host={Host} UserAgent={UserAgent} ContentType={ContentType} DeviceName={DeviceName}",
            HttpContext.Connection.RemoteIpAddress, Request.Headers["X-Forwarded-For"].ToString(),
            Request.Host, Request.Headers.UserAgent.ToString(), Request.ContentType, request?.DeviceName);

        try
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

            _log.LogInformation(
                "DeviceAuth.Initiate: succeeded — Code={Code} BaseUrl={BaseUrl} VerificationUrl={VerificationUrl}",
                result.Code, baseUrl, result.VerificationUrl);

            return Ok(ApiResponse<InitiateDeviceAuthResponseDto>.Ok(dto));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DeviceAuth.Initiate: threw an exception — RemoteIp={RemoteIp}",
                HttpContext.Connection.RemoteIpAddress);
            throw;
        }
    }

    /// <summary>Poll for approval. Called every ~5 seconds by the Kodi addon.</summary>
    [HttpGet("{code}/poll")]
    [AllowAnonymous]
    public async Task<IActionResult> Poll(string code)
    {
        _log.LogInformation("DeviceAuth.Poll: Code={Code} RemoteIp={RemoteIp} UserAgent={UserAgent}",
            code, HttpContext.Connection.RemoteIpAddress, Request.Headers.UserAgent.ToString());

        var result = await _deviceAuth.PollAsync(code);
        var dto    = new PollDeviceAuthResponseDto(result.Status, result.ApiKey);

        _log.LogInformation("DeviceAuth.Poll: Code={Code} Status={Status}", code, result.Status);

        return Ok(ApiResponse<PollDeviceAuthResponseDto>.Ok(dto));
    }

    /// <summary>Return a QR code PNG for the verification URL. Called by the Kodi addon.</summary>
    [HttpGet("{code}/qr")]
    [AllowAnonymous]
    public async Task<IActionResult> GetQr(string code)
    {
        _log.LogInformation("DeviceAuth.GetQr: Code={Code} RemoteIp={RemoteIp}",
            code, HttpContext.Connection.RemoteIpAddress);

        var url = await _deviceAuth.GetVerificationUrlAsync(code, GetBaseUrl());
        if (url is null)
        {
            _log.LogWarning("DeviceAuth.GetQr: Code={Code} not found", code);
            return NotFound();
        }

        using var generator = new QRCodeGenerator();
        var data   = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var qrCode = new PngByteQRCode(data);
        var rawPng = qrCode.GetGraphic(10);   // 10 px per module → ~310px for a typical QR

        // QRCoder's PngByteQRCode writes a 1-bit-per-pixel monochrome PNG via its own
        // minimal hand-rolled encoder. That's valid PNG (general viewers decode it fine),
        // but Kodi's texture loader renders it as nothing — no error, just a blank control.
        // A first pass just round-tripped through SkiaSharp's default decode/encode, but
        // SKBitmap.Decode preserves the source's grayscale color type by default -- it came
        // out as an 8-bit grayscale PNG, still not what Kodi wants. Drawing onto an explicit
        // Rgba8888 canvas forces a real color-type conversion, not just a bit-depth bump.
        using var source = SKBitmap.Decode(rawPng);
        using var rgba   = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(rgba))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, 0, 0);
        }
        using var image = SKImage.FromBitmap(rgba);
        using var data2 = image.Encode(SKEncodedImageFormat.Png, 100);
        var png = data2.ToArray();

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
