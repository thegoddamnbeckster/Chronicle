using System.ComponentModel.DataAnnotations;
using Chronicle.Core.Models;

namespace Chronicle.API.DTOs
{
    /// <summary>
    /// Full account view. Distinct from the slim <see cref="UserDto"/> returned by auth, which
    /// is the shape the frontend's session store already depends on and must not change.
    /// </summary>
    public record UserAccountDto(
        int Id,
        string Username,
        string? Email,
        string? FirstName,
        string? LastName,
        string? Handle,
        string? DisplayName,
        string ResolvedDisplayName,
        bool IsAdmin,
        bool IsActive,
        DateTime CreatedAt,
        DateTime? LastLoginAt,
        List<UserContactDto> Contacts
    )
    {
        public static UserAccountDto From(User u, IEnumerable<UserContact>? contacts = null) => new(
            u.Id, u.Username, u.Email, u.FirstName, u.LastName, u.Handle, u.DisplayName,
            u.ResolveDisplayName(), u.IsAdmin, u.IsActive, u.CreatedAt, u.LastLoginAt,
            (contacts ?? []).Select(UserContactDto.From).ToList());
    }

    public record UserContactDto(
        int Id, string Kind, string? Label, string Value, bool IsPrimary, DateTime CreatedAt)
    {
        public static UserContactDto From(UserContact c) =>
            new(c.Id, c.Kind, c.Label, c.Value, c.IsPrimary, c.CreatedAt);
    }

    public record CreateUserRequest(
        [Required, MinLength(3), MaxLength(50)] string Username,
        [Required, MinLength(8)] string Password,
        [EmailAddress] string? Email,
        string? FirstName,
        string? LastName,
        string? Handle,
        bool IsAdmin = false
    );

    /// <summary>
    /// Full replacement of the identity fields — a null/omitted field clears that value, which
    /// is what lets a user remove a handle they no longer want. Password and role are never
    /// part of this; they have their own endpoints so they can carry their own guards.
    /// </summary>
    public record UpdateProfileRequest(
        [EmailAddress] string? Email,
        string? FirstName,
        string? LastName,
        string? Handle,
        string? DisplayName
    );

    public record ChangePasswordRequest(
        string? CurrentPassword,
        [Required, MinLength(8)] string NewPassword
    );

    public record SetAdminRequest([Required] bool IsAdmin);

    public record SetActiveRequest([Required] bool IsActive);

    public record ContactRequest(
        [Required, MaxLength(50)] string Kind,
        [MaxLength(100)] string? Label,
        [Required, MaxLength(500)] string Value,
        bool IsPrimary = false
    );
}
