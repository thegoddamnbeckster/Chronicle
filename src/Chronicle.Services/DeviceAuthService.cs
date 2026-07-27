using System.Security.Cryptography;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Security;
using Microsoft.EntityFrameworkCore;


namespace Chronicle.Services;

public class DeviceAuthService : IDeviceAuthService
{
    private const int ExpirySeconds = 900;   // 15-minute window, matching SIMKL's own device-auth codes

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
        // This is a LAN pairing code for a handful of devices (Kodi installs, typically),
        // not a secret protecting an internet-facing service -- there's no need for the
        // 128-bit code this used to be, and a long opaque string in the verification URL
        // just makes it something nobody can type or read aloud. The short human code
        // (previously only a truncated *display* of a separate long code) is now the
        // one and only identifier, used everywhere: URL, QR payload, and DB lookup.
        var code = await GenerateUniqueCodeAsync();

        var expiry  = DateTime.UtcNow.AddSeconds(ExpirySeconds);

        var record = new DeviceAuthCode
        {
            Code        = code,
            DisplayCode = code,
            DeviceName  = deviceName?.Trim(),
            Status      = DeviceAuthStatus.Pending,
            ExpiresAt   = expiry,
            CreatedAt   = DateTime.UtcNow,
        };

        _db.DeviceAuthCodes.Add(record);
        await _db.SaveChangesAsync();

        var verificationUrl = $"{baseUrl.TrimEnd('/')}/a/{code}";

        return new InitiateDeviceAuthResult(
            code, code, verificationUrl, expiry, ExpirySeconds);
    }

    // Excludes 0/O, 1/I/L and similar look-alikes -- this gets read off a screen and
    // typed on a phone or TV remote, so avoiding ambiguous characters matters more than
    // maximizing entropy. 32 symbols ^ 6 chars ~= 1.07 billion combinations, still far
    // more than enough headroom for a handful of concurrently pending codes.
    private const string CodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const int CodeLength = 6;

    /// <summary>
    /// 6-char unambiguous-alphabet code, e.g. "7HKQ2M" -- no dashes, short enough to read
    /// aloud or type in one go (matches the brevity of SIMKL's own pairing codes). Checked
    /// against currently pending codes and regenerated on the rare collision rather than
    /// assumed unique.
    /// </summary>
    private async Task<string> GenerateUniqueCodeAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var rawBytes = RandomNumberGenerator.GetBytes(CodeLength);
            var chars    = new char[CodeLength];
            for (var i = 0; i < CodeLength; i++)
                chars[i] = CodeAlphabet[rawBytes[i] % CodeAlphabet.Length];
            var code = new string(chars);

            var collision = await _db.DeviceAuthCodes.AnyAsync(c =>
                c.Code == code && c.Status == DeviceAuthStatus.Pending);
            if (!collision)
                return code;
        }

        throw new InvalidOperationException("Could not generate a unique device-auth code.");
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
            ? $"{baseUrl.TrimEnd('/')}/a/{code}"
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
