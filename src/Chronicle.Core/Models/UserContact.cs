namespace Chronicle.Core.Models
{
    /// <summary>
    /// One way to reach a user. Deliberately generic: <see cref="Kind"/> is a free-form string
    /// rather than an enum, so a new contact method (a new social network, a work extension, a
    /// Matrix ID) needs no schema migration and no code change — the same reasoning behind
    /// Chronicle's generic media model. A user can hold any number of each kind.
    /// </summary>
    public class UserContact
    {
        public int Id { get; set; }
        public int UserId { get; set; }

        /// <summary>What sort of contact this is — "email", "phone", "mastodon", "discord",
        /// "website", or anything else. Stored lowercase for consistent grouping.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Optional user-facing note distinguishing several of the same kind —
        /// "work", "mobile", "personal".</summary>
        public string? Label { get; set; }

        /// <summary>The address/number/handle/URL itself.</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>Marks the preferred entry within its kind (one primary phone, one primary
        /// email, ...). Enforced per (user, kind) by the service, not the schema.</summary>
        public bool IsPrimary { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public User? User { get; set; }
    }
}
