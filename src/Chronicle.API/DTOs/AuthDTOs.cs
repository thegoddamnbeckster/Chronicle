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

    public record UserDto(int Id, string Username, string? Email, string? DisplayName, bool IsAdmin, bool ShowDiagnostics);
}
