namespace Chronicle.Core.Models
{
    public class ApiToken
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Name { get; set; } = string.Empty;

        /// <summary>Hashed token value stored in the database.</summary>
        public string Token { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool IsActive { get; set; } = true;

        // Navigation
        public User? User { get; set; }
    }
}
