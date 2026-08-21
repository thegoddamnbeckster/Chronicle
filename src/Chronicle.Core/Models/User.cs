namespace Chronicle.Core.Models
{
    public class User
    {
        public int Id { get; set; }

        /// <summary>Login credential. Stable — changing how a user is *shown* is what Handle is for.</summary>
        public string Username { get; set; } = string.Empty;

        public string? Email { get; set; }
        public string PasswordHash { get; set; } = string.Empty;

        /// <summary>Legacy free-text display override. Retained so existing rows keep working, but
        /// ResolveDisplayName is the thing to call — it applies the handle/name/username fallback.</summary>
        public string? DisplayName { get; set; }

        public string? FirstName { get; set; }
        public string? LastName { get; set; }

        /// <summary>Public identifier (e.g. "jsmith"). Freely changeable — unlike Username, nothing
        /// authenticates against it.</summary>
        public string? Handle { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsAdmin { get; set; } = false;
        public string PreferencesJson { get; set; } = "{}";

        /// <summary>Phone numbers, social profiles, extra emails — anything, via a free-form Kind.</summary>
        public ICollection<UserContact> Contacts { get; set; } = new List<UserContact>();

        /// <summary>
        /// How this user should be shown anywhere in the UI: handle first, then first+last name,
        /// then the login username as the guaranteed-present fallback. An explicitly-set
        /// DisplayName still wins, so anyone who already set one keeps it.
        /// </summary>
        public string ResolveDisplayName()
        {
            if (!string.IsNullOrWhiteSpace(DisplayName)) return DisplayName.Trim();
            if (!string.IsNullOrWhiteSpace(Handle))       return Handle.Trim();

            var full = $"{FirstName?.Trim()} {LastName?.Trim()}".Trim();
            return !string.IsNullOrWhiteSpace(full) ? full : Username;
        }
    }
}
