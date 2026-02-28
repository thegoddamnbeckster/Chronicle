using System.Security.Cryptography;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Security;
using Microsoft.EntityFrameworkCore;


namespace Chronicle.Services;

public class DeviceAuthService : IDeviceAuthService
{
    private const int ExpirySeconds = 300;   // 5-minute window

    private readonly ChronicleDbContext _db;
    private readonly IApiTokenService   _apiTokenService;

    public DeviceAuthService(ChronicleDbContext db, IApiTokenService apiTokenService)
    {
        _db              = db;
        _apiTokenService = apiTokenService;
    }

    // ── Initiate ─────────────────────────────────────────────────────────────

    public async Task<InitiateDeviceAuthResult> InitiateAsync(string? deviceName, string baseUrl)
    {
        // Generate cryptographically random 32-char hex code
        var rawBytes = RandomNumberGenerator.GetBytes(16);
        var code     = Convert.ToHexString(rawBytes).ToLowerInvariant();

        // Human-readable 8-char display code (first 8 hex chars, split A1B2-C3D4)
        var display = code[..4].ToUpperInvariant() + '-' + code[4..8].ToUpperInvariant();

        var expiry  = DateTime.UtcNow.AddSeconds(ExpirySeconds);

        var record = new DeviceAuthCode
        {
            Code        = code,
            DisplayCode = display,
            DeviceName  = deviceName?.Trim(),
            Status      = DeviceAuthStatus.Pending,
            ExpiresAt   = expiry,
            CreatedAt   = DateTime.UtcNow,
        };

        _db.DeviceAuthCodes.Add(record);
        await _db.SaveChangesAsync();

        var verificationUrl = $"{baseUrl.TrimEnd('/')}/device-auth/{code}";

        return new InitiateDeviceAuthResult(
            code, display, verificationUrl, expiry, ExpirySeconds);
    }

    // ── Info (for the approval page) ─────────────────────────────────────────

    public async Task<DeviceAuthInfoResult?> GetInfoAsync(string code)
    {
        var record = await _db.DeviceAuthCodes
            .FirstOrDefaultAsync(c => c.Code == code);

        if (record is null)
            return null;

        // Expire in-memory if time has passed
        if (record.Status == DeviceAuthStatus.Pending && record.ExpiresAt < DateTime.UtcNow)
        {
            record.Status = DeviceAuthStatus.Expired;
            await _db.SaveChangesAsync();
        }

        return new DeviceAuthInfoResult(
            record.DisplayCode,
            record.DeviceName,
            record.Status.ToString().ToLowerInvariant(),
            record.ExpiresAt);
    }

    // ── Approve ───────────────────────────────────────────────────────────────

    public async Task ApproveAsync(int userId, string code)
    {
        var record = await FindPendingAsync(code);

        var tokenName = string.IsNullOrWhiteSpace(record.DeviceName)
            ? "Device (QR Auth)"
            : $"{record.DeviceName} (QR Auth)";

        var (apiToken, rawKey) = await _apiTokenService
            .CreateTokenAsync(userId, tokenName, expiresAt: null);

        record.Status      = DeviceAuthStatus.Approved;
        record.UserId      = userId;
        record.ApiTokenId  = apiToken.Id;
        record.RawApiKey   = rawKey;   // Cleared after first poll
        record.ApprovedAt  = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    // ── Deny ──────────────────────────────────────────────────────────────────

    public async Task DenyAsync(int userId, string code)
    {
        var record = await FindPendingAsync(code);
        record.Status = DeviceAuthStatus.Denied;
        await _db.SaveChangesAsync();
    }

    // ── Poll ──────────────────────────────────────────────────────────────────

    public async Task<PollDeviceAuthResult> PollAsync(string code)
    {
        var record = await _db.DeviceAuthCodes
            .FirstOrDefaultAsync(c => c.Code == code);

        if (record is null)
            return new PollDeviceAuthResult("expired", null);

        // Lazily expire
        if (record.Status == DeviceAuthStatus.Pending && record.ExpiresAt < DateTime.UtcNow)
        {
            record.Status = DeviceAuthStatus.Expired;
            await _db.SaveChangesAsync();
            return new PollDeviceAuthResult("expired", null);
        }

        switch (record.Status)
        {
            case DeviceAuthStatus.Pending:
                return new PollDeviceAuthResult("pending", null);

            case DeviceAuthStatus.Approved:
                // Return raw key once, then transition to Retrieved
                var rawKey      = record.RawApiKey;
                record.Status   = DeviceAuthStatus.Retrieved;
                record.RawApiKey = null;
                await _db.SaveChangesAsync();
                return new PollDeviceAuthResult("approved", rawKey);

            case DeviceAuthStatus.Retrieved:
                // Device already collected the key
                return new PollDeviceAuthResult("approved", null);

            case DeviceAuthStatus.Denied:
                return new PollDeviceAuthResult("denied", null);

            default:   // Expired
                return new PollDeviceAuthResult("expired", null);
        }
    }

    // ── Verification URL ──────────────────────────────────────────────────────

    public async Task<string?> GetVerificationUrlAsync(string code, string baseUrl)
    {
        var exists = await _db.DeviceAuthCodes
            .AnyAsync(c => c.Code == code);

        return exists
            ? $"{baseUrl.TrimEnd('/')}/device-auth/{code}"
            : null;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public async Task CleanupExpiredAsync()
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);

        var old = await _db.DeviceAuthCodes
            .Where(c => c.ExpiresAt < cutoff)
            .ToListAsync();

        _db.DeviceAuthCodes.RemoveRange(old);
        await _db.SaveChangesAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<DeviceAuthCode> FindPendingAsync(string code)
    {
        var record = await _db.DeviceAuthCodes
            .FirstOrDefaultAsync(c => c.Code == code)
            ?? throw new DeviceAuthCodeNotFoundException();

        if (record.ExpiresAt < DateTime.UtcNow)
        {
            record.Status = DeviceAuthStatus.Expired;
            await _db.SaveChangesAsync();
            throw new DeviceAuthCodeExpiredException();
        }

        if (record.Status != DeviceAuthStatus.Pending)
            throw new DeviceAuthCodeAlreadyUsedException();

        return record;
    }
}
