using System.ComponentModel.DataAnnotations;

namespace Chronicle.API.DTOs;

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record InitiateDeviceAuthRequestDto(
    /// <summary>Human-readable device name shown on the approval page, e.g. "Kodi Living Room".</summary>
    [MaxLength(100)] string? DeviceName
);

// ── Response DTOs ─────────────────────────────────────────────────────────────

public record InitiateDeviceAuthResponseDto(
    string Code,
    string DisplayCode,
    string VerificationUrl,
    string QrUrl,
    DateTime ExpiresAt,
    int ExpiresInSeconds
);

public record DeviceAuthInfoDto(
    string DisplayCode,
    string? DeviceName,
    string Status,
    DateTime ExpiresAt
);

public record PollDeviceAuthResponseDto(
    string Status,
    string? ApiKey
);
