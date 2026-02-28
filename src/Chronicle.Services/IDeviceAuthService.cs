using Chronicle.Core.Models;

namespace Chronicle.Services;

public record InitiateDeviceAuthResult(
    string Code,
    string DisplayCode,
    string VerificationUrl,
    DateTime ExpiresAt,
    int ExpiresInSeconds
);

public record PollDeviceAuthResult(
    /// <summary>"pending" | "approved" | "denied" | "expired"</summary>
    string Status,
    /// <summary>Raw API key — only present on the FIRST approved poll.</summary>
    string? ApiKey
);

public record DeviceAuthInfoResult(
    string DisplayCode,
    string? DeviceName,
    string Status,
    DateTime ExpiresAt
);

public interface IDeviceAuthService
{
    /// <summary>
    /// Generates a new device-auth code and returns the QR verification URL.
    /// No authentication required — called by the device itself.
    /// </summary>
    Task<InitiateDeviceAuthResult> InitiateAsync(string? deviceName, string baseUrl);

    /// <summary>
    /// Returns basic info about the code so the approval page can display it.
    /// Requires the user to be logged in.
    /// </summary>
    Task<DeviceAuthInfoResult?> GetInfoAsync(string code);

    /// <summary>
    /// Marks the code as approved, creates an API token for the device, and
    /// stores the raw token temporarily for the device to retrieve via Poll.
    /// Requires the user to be logged in.
    /// </summary>
    Task ApproveAsync(int userId, string code);

    /// <summary>Marks the code as denied.</summary>
    Task DenyAsync(int userId, string code);

    /// <summary>
    /// Called repeatedly by the device. Returns the raw API key on the first
    /// successful Approved poll; clears it afterwards (status → Retrieved).
    /// </summary>
    Task<PollDeviceAuthResult> PollAsync(string code);

    /// <summary>
    /// Returns the verification URL for a given code (used by the controller to generate a QR image).
    /// Returns null if the code does not exist.
    /// </summary>
    Task<string?> GetVerificationUrlAsync(string code, string baseUrl);

    /// <summary>Removes codes that expired more than 24 hours ago.</summary>
    Task CleanupExpiredAsync();
}
