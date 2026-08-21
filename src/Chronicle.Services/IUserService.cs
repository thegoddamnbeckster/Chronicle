using Chronicle.Core.Models;

namespace Chronicle.Services
{
    public interface IUserService
    {
        Task<User> AuthenticateAsync(string username, string password);
        Task<User> RegisterAsync(string username, string password, string? email);
        Task<User?> GetByIdAsync(int id);
        Task<User?> GetByUsernameAsync(string username);
        Task<UserPreferences> GetPreferencesAsync(int userId);
        Task UpdatePreferencesAsync(int userId, UserPreferences patch);

        // ── Administration ────────────────────────────────────────────────────
        // Every mutation below refuses to remove the last remaining active admin, whichever
        // route is taken (demote, deactivate, delete). Without that, one wrong click locks
        // everyone out of admin functions permanently with no in-app way back.

        Task<List<User>> ListUsersAsync(CancellationToken ct = default);

        /// <summary>Admin-created account. Unlike registration, never auto-promotes.</summary>
        Task<User> CreateUserAsync(string username, string password, string? email,
            string? firstName, string? lastName, string? handle, bool isAdmin, CancellationToken ct = default);

        /// <summary>Promote/demote. Throws when it would remove the last active admin.</summary>
        Task<User> SetAdminAsync(int userId, bool isAdmin, CancellationToken ct = default);

        /// <summary>Reversible suspension — blocks login, keeps every row intact.</summary>
        Task<User> SetActiveAsync(int userId, bool isActive, CancellationToken ct = default);

        /// <summary>
        /// Irreversible. Cascades the account's own rows (library, watch history, API tokens,
        /// lists) and clears the audit stamps that reference it but carry no FK
        /// (media_item_merges.merged_by_user_id, artwork overrides' pinnedByUserId) so nothing
        /// is left pointing at a user that no longer exists. Shared media is untouched.
        /// </summary>
        Task DeleteUserAsync(int userId, CancellationToken ct = default);

        // ── Profile (self-service or admin-on-behalf) ─────────────────────────

        Task<User> UpdateProfileAsync(int userId, string? email, string? firstName,
            string? lastName, string? handle, string? displayName, CancellationToken ct = default);

        /// <summary>Confirms a password without logging the user in (no LastLoginAt stamp).</summary>
        Task<bool> VerifyPasswordAsync(int userId, string password, CancellationToken ct = default);

        Task ChangePasswordAsync(int userId, string newPassword, CancellationToken ct = default);

        // ── Contact methods ───────────────────────────────────────────────────

        Task<List<UserContact>> ListContactsAsync(int userId, CancellationToken ct = default);
        Task<UserContact> AddContactAsync(int userId, string kind, string? label, string value,
            bool isPrimary, CancellationToken ct = default);
        Task<UserContact> UpdateContactAsync(int userId, int contactId, string kind, string? label,
            string value, bool isPrimary, CancellationToken ct = default);
        Task DeleteContactAsync(int userId, int contactId, CancellationToken ct = default);
    }
}
