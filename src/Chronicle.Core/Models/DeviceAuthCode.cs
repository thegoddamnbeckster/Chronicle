namespace Chronicle.Core.Models;

public enum DeviceAuthStatus
{
    Pending,    // Waiting for user to approve or deny
    Approved,   // User approved; raw API key awaiting first retrieval
    Retrieved,  // Device polled and received the API key (terminal)
    Denied,     // User explicitly denied (terminal)
    Expired,    // Code expired before user acted (terminal)
}

/// <summary>
/// A short-lived device-auth code that lets a headless Kodi/scrobbler device
/// authenticate without a keyboard, using a QR-code flow similar to OAuth 2.0
/// Device Authorization Grant (RFC 8628).
///
/// Flow:
///   1. Device POSTs /api/v1/auth/device → gets <see cref="Code"/> + QR URL.
///   2. User scans QR on their phone, logs in to Chronicle, clicks "Allow".
///   3. Device polls /api/v1/auth/device/{code}/poll until Status == Approved.
///   4. On first Approved poll the raw API key is returned; <see cref="RawApiKey"/>
///      is then cleared and <see cref="Status"/> becomes Retrieved.
/// </summary>
public class DeviceAuthCode
{
    public int Id { get; set; }

    /// <summary>Cryptographically random 32-char hex string embedded in the QR URL.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable 8-char code shown on screen, e.g. "A1B2-C3D4".</summary>
    public string DisplayCode { get; set; } = string.Empty;

    /// <summary>Optional friendly name sent by the device, e.g. "Kodi Living Room".</summary>
    public string? DeviceName { get; set; }

    public DeviceAuthStatus Status { get; set; } = DeviceAuthStatus.Pending;

    /// <summary>
    /// The raw (unhashed) API key — stored only between approval and first retrieval.
    /// Cleared once the device has polled and received the key.
    /// </summary>
    public string? RawApiKey { get; set; }

    public int? UserId { get; set; }
    public int? ApiTokenId { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }

    // Navigation
    public User? User { get; set; }
    public ApiToken? ApiToken { get; set; }
}
