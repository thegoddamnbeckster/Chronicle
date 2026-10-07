using System.ComponentModel.DataAnnotations;

namespace Chronicle.API.DTOs
{
    public record RegisterRequest(
        [Required, MinLength(3), MaxLength(50)] string Username,
        [Required, MinLength(8)] string Password,
        [EmailAddress] string? Email
    );

    public record LoginRequest(
        [Required] string Username,
        [Required] string Password
    );

    public record AuthResponse(string Token, UserDto User);

    public record ForgotPasswordRequest([Required, MaxLength(320)] string Identifier);

    public record ResetPasswordRequest(
        [Required, MaxLength(200)] string Token,
        [Required, MinLength(8), MaxLength(200)] string NewPassword);

    /// <summary>Shown once to the administrator who asked for it.</summary>
    public record ResetTokenDto(string Token, DateTime ExpiresAt, string ResetUrl, string Username);

    /// <summary>A signed-in session as shown in the sessions list. Never carries the key.</summary>
    public record SessionDto(Guid Id, DateTime CreatedAt, DateTime LastSeenAt, DateTime ExpiresAt,
        string? UserAgent, string? RemoteIp, bool IsCurrent);

    public record UserDto(int Id, string Username, string? Email, string? DisplayName, bool IsAdmin, bool ShowDiagnostics,
        bool ShowNowPlayingBanner = true, bool ShowAllCredits = false);
}
