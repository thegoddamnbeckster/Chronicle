using System.ComponentModel.DataAnnotations;

namespace Chronicle.API.DTOs;

/// <summary>Safe representation of a token returned in list responses (no raw value).</summary>
public record ApiTokenDto(
    int Id,
    string Name,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? ExpiresAt
);

/// <summary>Request body for creating a new API token.</summary>
public record CreateApiTokenRequest(
    [Required, MinLength(1), MaxLength(100)] string Name,
    DateTime? ExpiresAt
);

/// <summary>
/// Response after creating a token — includes the one-time-visible raw token value.
/// </summary>
public record CreateApiTokenResponse(
    int Id,
    string Name,
    /// <summary>The raw <c>chr_live_…</c> key — show once, never stored.</summary>
    string Token,
    DateTime CreatedAt,
    DateTime? ExpiresAt
);
