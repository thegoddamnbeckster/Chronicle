using Chronicle.Core.Models;

namespace Chronicle.Services.Security;

public interface IApiTokenService
{
    /// <summary>
    /// Creates a new API token for the given user.
    /// Returns the persisted token (with its database Id) and the one-time-visible raw value.
    /// The raw value is NEVER stored — callers must show it to the user immediately.
    /// </summary>
    Task<(ApiToken Token, string RawValue)> CreateTokenAsync(int userId, string name, DateTime? expiresAt);

    /// <summary>
    /// Validates a raw API key supplied in the X-API-Key header.
    /// Returns the matching <see cref="ApiToken"/> (User navigation loaded) if valid;
    /// <c>null</c> if the key is unknown, revoked, or expired.
    /// Also updates <see cref="ApiToken.LastUsedAt"/> on success.
    /// </summary>
    Task<ApiToken?> ValidateTokenAsync(string rawToken);

    /// <summary>Returns all active (non-revoked) tokens owned by the user.</summary>
    Task<List<ApiToken>> GetTokensForUserAsync(int userId);

    /// <summary>
    /// Soft-deletes (revokes) the token with the given id, provided it belongs to userId.
    /// Returns <c>false</c> if the token was not found or already revoked.
    /// </summary>
    Task<bool> RevokeTokenAsync(int tokenId, int userId);
}
