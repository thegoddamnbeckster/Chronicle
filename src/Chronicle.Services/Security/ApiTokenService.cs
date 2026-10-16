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
        int userId, string name, DateTime? expiresAt, CancellationToken ct = default, string scope = ApiKeyScopes.Full)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Token name must not be empty.", nameof(name));
        if (!ApiKeyScopes.IsKnown(scope))
            throw new ArgumentException($"Unknown API key scope '{scope}'.", nameof(scope));

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
            IsActive = true,
            Scope = scope,
        };

        _db.ApiTokens.Add(token);
        await _db.SaveChangesAsync(ct);

        return (token, rawToken);
    }

    /// <inheritdoc/>
    public async Task<ApiToken?> ValidateTokenAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || !rawToken.StartsWith("chr_live_", StringComparison.Ordinal))
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

        // Update last-used timestamp — debounced to at most once per 5 minutes so
        // high-frequency scrobbler requests don't generate a DB write on every call.
        var now = DateTime.UtcNow;
        if (token.LastUsedAt is null || (now - token.LastUsedAt.Value).TotalMinutes >= 5)
            await TryRecordUseAsync(token, now, ct);

        return token;
    }

    /// <summary>How long the "last used" stamp may wait for SQLite's single writer before it is skipped.</summary>
    internal static readonly TimeSpan LastUsedWriteBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Stamps the key as used. Cosmetic, and done inside authentication, so it must never cost a request: it was a plain
    /// SaveChanges, which under writer contention (the nightly tasks) waited up to ~34 s for the lock and then threw out of the
    /// auth handler -- every Kodi poll sharing that moment stalled or failed (kodi-scan-signal averaged 6 s, scan-active 3.7 s,
    /// maxima at 34 s). Now it gets a couple of seconds and is simply skipped when the database is busy; the next request tries again.
    /// </summary>
    private async Task TryRecordUseAsync(ApiToken token, DateTime now, CancellationToken ct)
    {
        var previous = token.LastUsedAt;
        var relational = _db.Database.IsRelational();
        var previousTimeout = relational ? _db.Database.GetCommandTimeout() : null;
        token.LastUsedAt = now;
        try
        {
            if (relational) _db.Database.SetCommandTimeout(LastUsedWriteBudget);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is DbUpdateException || MetadataEnrichmentService.IsDatabaseBusy(ex)))
        {
            // Leave nothing pending on the context the rest of the request shares.
            _db.Entry(token).State = EntityState.Unchanged;
            token.LastUsedAt = previous;
        }
        finally
        {
            if (relational) _db.Database.SetCommandTimeout(previousTimeout);
        }
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

    public async Task<bool> SetScopeAsync(int tokenId, int userId, string scope, CancellationToken ct = default)
    {
        if (!ApiKeyScopes.IsKnown(scope))
            throw new ArgumentException($"Unknown API key scope '{scope}'.", nameof(scope));

        var token = await _db.ApiTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId && t.IsActive, ct);
        if (token is null)
            return false;

        token.Scope = scope;
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
