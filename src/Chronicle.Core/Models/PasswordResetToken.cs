namespace Chronicle.Core.Models;

/// <summary>
/// A single-use, expiring credential that lets someone set a new password without knowing the old one.
/// Only a SHA-256 hash of the token is stored, so a copy of the database cannot be used to reset anyone's password.
/// </summary>
public class PasswordResetToken
{
    public int Id { get; set; }
    public int UserId { get; set; }

    /// <summary>SHA-256 of the raw token, upper-case hex.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set when the token has been spent. A spent token can never be used again.</summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>The administrator who issued it, or null when the user asked for it themselves.</summary>
    public int? IssuedByUserId { get; set; }

    /// <summary>How the token reached the person: "email", "admin" (handed over by an administrator) or "console" (the local recovery command).</summary>
    public string Delivery { get; set; } = "email";

    public User? User { get; set; }
}
