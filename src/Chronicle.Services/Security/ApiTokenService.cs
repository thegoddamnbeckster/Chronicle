using System.Security.Cryptography;
using System.Text;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services.Security;

/// <inheritdoc cref="IApiTokenService"/>
public class ApiTokenService : IApiTokenService
{
    private readonly ChronicleDbContext _db;

    public ApiTokenService(ChronicleDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task<(ApiToken Token, string RawValue)> CreateTokenAsync(
        int userId, string name, DateTime? expiresAt, CancellationToken ct = default)
    {
        // Format: chr_live_ + 32 lowercase hex chars (16 cryptographically random bytes)
        var rawBytes = RandomNumberGenerator.GetBytes(16);
        var rawToken = "chr_live_" + Convert.ToHexString(rawBytes).ToLowerInvariant();

        var token = new ApiToken
        {
            UserId = userId,
            Name = name,
            Token = HashToken(rawToken),   // Only the SHA-256 hash is persisted
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            IsActive = true
        };

        _db.ApiTokens.Add(token);
        await _db.SaveChangesAsync(ct);

        return (token, rawToken);
    }

    /// <inheritdoc/>
    public async Task<ApiToken?> ValidateTokenAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || !rawToken.StartsWith("chr_live_"))
            return null;

        var hash = HashToken(rawToken);

        var token = await _db.ApiTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Token == hash && t.IsActive, ct);

        if (token is null)
            return null;

        // Enforce expiry
        if (token.ExpiresAt.HasValue && token.ExpiresAt.Value < DateTime.UtcNow)
            return null;

        // Update last-used timestamp
        token.LastUsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return token;
    }

    /// <inheritdoc/>
    public async Task<List<ApiToken>> GetTokensForUserAsync(int userId, CancellationToken ct = default)
    {
        return await _db.ApiTokens
            .Where(t => t.UserId == userId && t.IsActive)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<bool> RevokeTokenAsync(int tokenId, int userId, CancellationToken ct = default)
    {
        var token = await _db.ApiTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId && t.IsActive, ct);

        if (token is null)
            return false;

        token.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>SHA-256 hash of the raw token string, hex-encoded, lowercase.</summary>
    private static string HashToken(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
