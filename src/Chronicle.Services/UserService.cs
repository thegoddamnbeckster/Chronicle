using System.Text.Json;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Security;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Chronicle.Services
{
    public class UserService : IUserService
    {
        private readonly ILogger _log = Log.ForContext<UserService>();
        private readonly ChronicleDbContext _context;
        private readonly IPasswordHasher _passwordHasher;
        private readonly IDeactivatedUserCache _deactivated;

        public UserService(ChronicleDbContext context, IPasswordHasher passwordHasher,
            IDeactivatedUserCache deactivated)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _deactivated = deactivated;
        }

        public async Task<User> AuthenticateAsync(string username, string password)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Username == username && u.IsActive);

            if (user == null || !_passwordHasher.VerifyPassword(password, user.PasswordHash))
                throw new InvalidCredentialsException();

            user.LastLoginAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return user;
        }

        public async Task<User> RegisterAsync(string username, string password, string? email)
        {
            if (await _context.Users.AnyAsync(u => u.Username == username))
                throw new DuplicateUsernameException(username);

            var isFirstUser = !await _context.Users.AnyAsync();

            var user = new User
            {
                Username = username,
                Email = email,
                PasswordHash = _passwordHasher.HashPassword(password),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                IsActive = true,
                IsAdmin = isFirstUser
            };

            _context.Users.Add(user);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Unique constraint on Username was violated by a concurrent registration.
                throw new DuplicateUsernameException(username);
            }

            // See CreateUserAsync — a reused row id must not inherit a deleted user's block.
            _deactivated.Unblock(user.Id);

            return user;
        }

        public async Task<User?> GetByIdAsync(int id)
        {
            return await _context.Users.FindAsync(id);
        }

        public async Task<User?> GetByUsernameAsync(string username)
        {
            return await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
        }

        public async Task<UserPreferences> GetPreferencesAsync(int userId)
        {
            var user = await _context.Users.FindAsync(userId)
                ?? throw new UserNotFoundException(userId.ToString());
            try { return JsonSerializer.Deserialize<UserPreferences>(user.PreferencesJson) ?? new(); }
            catch (JsonException ex) { _log.Warning(ex, "Failed to deserialize preferences for user {UserId} — using defaults", userId); return new(); }
        }

        public async Task UpdatePreferencesAsync(int userId, UserPreferences patch)
        {
            var user = await _context.Users.FindAsync(userId)
                ?? throw new UserNotFoundException(userId.ToString());
            UserPreferences current;
            try { current = JsonSerializer.Deserialize<UserPreferences>(user.PreferencesJson) ?? new(); }
            catch (JsonException ex) { _log.Warning(ex, "Failed to deserialize preferences for user {UserId} during update — starting fresh", userId); current = new(); }
            if (patch.ShowDiagnostics.HasValue) current.ShowDiagnostics = patch.ShowDiagnostics;

            if (patch.CreateCollectionStubs.HasValue) current.CreateCollectionStubs = patch.CreateCollectionStubs;

            if (patch.ShowNowPlayingBanner.HasValue) current.ShowNowPlayingBanner = patch.ShowNowPlayingBanner;

            if (patch.DefaultFoldsOpen.HasValue)
                current.DefaultFoldsOpen = patch.DefaultFoldsOpen;

            if (patch.Folds is { Count: > 0 })
            {
                current.Folds ??= new Dictionary<string, bool>();
                foreach (var (key, value) in patch.Folds)
                    current.Folds[key] = value;
            }

            if (!string.IsNullOrEmpty(patch.Theme)) current.Theme = patch.Theme;

            user.PreferencesJson = JsonSerializer.Serialize(current);
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        // ── Administration ────────────────────────────────────────────────────

        public async Task<List<User>> ListUsersAsync(CancellationToken ct = default) =>
            await _context.Users.OrderBy(u => u.Username).ToListAsync(ct);

        public async Task<User> CreateUserAsync(string username, string password, string? email,
            string? firstName, string? lastName, string? handle, bool isAdmin, CancellationToken ct = default)
        {
            if (await _context.Users.AnyAsync(u => u.Username == username, ct))
                throw new DuplicateUsernameException(username);

            var user = new User
            {
                Username     = username,
                Email        = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
                FirstName    = Clean(firstName),
                LastName     = Clean(lastName),
                Handle       = Clean(handle),
                PasswordHash = _passwordHasher.HashPassword(password),
                CreatedAt    = DateTime.UtcNow,
                UpdatedAt    = DateTime.UtcNow,
                IsActive     = true,
                // Unlike RegisterAsync, never auto-promotes: an admin creating an account says
                // explicitly whether it's an admin.
                IsAdmin      = isAdmin,
            };

            _context.Users.Add(user);
            try { await _context.SaveChangesAsync(ct); }
            catch (DbUpdateException) { throw new DuplicateUsernameException(username); }

            // SQLite can hand a new row the id of a deleted one, so clear any stale block
            // rather than lock out a brand-new account.
            _deactivated.Unblock(user.Id);

            _log.Information("Created user {UserId} ({Username}), admin={IsAdmin}", user.Id, username, isAdmin);
            return user;
        }

        public async Task<User> SetAdminAsync(int userId, bool isAdmin, CancellationToken ct = default)
        {
            var user = await _context.Users.FindAsync([userId], ct) ?? throw new UserNotFoundException(userId);

            if (!isAdmin && user.IsAdmin && await IsLastActiveAdminAsync(userId, ct))
                throw new LastAdminException("demote");

            user.IsAdmin   = isAdmin;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            _log.Information("User {UserId} ({Username}) admin set to {IsAdmin}", user.Id, user.Username, isAdmin);
            return user;
        }

        public async Task<User> SetActiveAsync(int userId, bool isActive, CancellationToken ct = default)
        {
            var user = await _context.Users.FindAsync([userId], ct) ?? throw new UserNotFoundException(userId);

            if (!isActive && user.IsAdmin && await IsLastActiveAdminAsync(userId, ct))
                throw new LastAdminException("deactivate");

            user.IsActive  = isActive;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            // Cuts off any JWT already in the wild — otherwise the suspended session keeps
            // working until the token happens to expire.
            if (isActive) _deactivated.Unblock(userId); else _deactivated.Block(userId);

            _log.Information("User {UserId} ({Username}) active set to {IsActive}", user.Id, user.Username, isActive);
            return user;
        }

        public async Task DeleteUserAsync(int userId, CancellationToken ct = default)
        {
            var user = await _context.Users.FindAsync([userId], ct) ?? throw new UserNotFoundException(userId);

            if (user.IsAdmin && await IsLastActiveAdminAsync(userId, ct))
                throw new LastAdminException("delete");

            // merged_by_user_id is a bare column with no FK behind it, so deleting the user would
            // otherwise leave it pointing at an account that no longer exists. Clear it rather
            // than leave a dangling reference. (Personal rows — library, events, tokens, lists —
            // are removed by their own cascades; shared media_items are never user-owned.)
            var stamped = await _context.MediaItemMerges
                .Where(m => m.MergedByUserId == userId)
                .ToListAsync(ct);
            foreach (var m in stamped) m.MergedByUserId = null;

            // The FK cascade would take these too, but removing them explicitly keeps the
            // intent visible and holds regardless of provider.
            var contacts = await _context.UserContacts.Where(c => c.UserId == userId).ToListAsync(ct);
            _context.UserContacts.RemoveRange(contacts);

            _context.Users.Remove(user);
            await _context.SaveChangesAsync(ct);

            _deactivated.Block(userId);

            _log.Information(
                "Deleted user {UserId} ({Username}); cleared {Count} merge audit stamp(s)",
                userId, user.Username, stamped.Count);
        }

        /// <summary>True when this user is an admin and no OTHER active admin exists.</summary>
        private async Task<bool> IsLastActiveAdminAsync(int userId, CancellationToken ct) =>
            !await _context.Users.AnyAsync(u => u.Id != userId && u.IsAdmin && u.IsActive, ct);

        // ── Profile ───────────────────────────────────────────────────────────

        public async Task<User> UpdateProfileAsync(int userId, string? email, string? firstName,
            string? lastName, string? handle, string? displayName, CancellationToken ct = default)
        {
            var user = await _context.Users.FindAsync([userId], ct) ?? throw new UserNotFoundException(userId);

            user.Email       = Clean(email);
            user.FirstName   = Clean(firstName);
            user.LastName    = Clean(lastName);
            user.Handle      = Clean(handle);
            user.DisplayName = Clean(displayName);
            user.UpdatedAt   = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);
            return user;
        }

        /// <summary>
        /// Confirms a password without the side effects of a login — notably, it does not
        /// stamp LastLoginAt, so "changed my password" doesn't masquerade as a sign-in.
        /// </summary>
        public async Task<bool> VerifyPasswordAsync(int userId, string password, CancellationToken ct = default)
        {
            var user = await _context.Users.FindAsync([userId], ct);
            return user is not null && _passwordHasher.VerifyPassword(password, user.PasswordHash);
        }

        public async Task ChangePasswordAsync(int userId, string newPassword, CancellationToken ct = default)
        {
            var user = await _context.Users.FindAsync([userId], ct) ?? throw new UserNotFoundException(userId);
            user.PasswordHash = _passwordHasher.HashPassword(newPassword);
            user.UpdatedAt    = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            _log.Information("Password changed for user {UserId} ({Username})", userId, user.Username);
        }

        // ── Contacts ──────────────────────────────────────────────────────────

        public async Task<List<UserContact>> ListContactsAsync(int userId, CancellationToken ct = default) =>
            await _context.UserContacts
                .Where(c => c.UserId == userId)
                .OrderBy(c => c.Kind).ThenByDescending(c => c.IsPrimary).ThenBy(c => c.Id)
                .ToListAsync(ct);

        public async Task<UserContact> AddContactAsync(int userId, string kind, string? label,
            string value, bool isPrimary, CancellationToken ct = default)
        {
            if (!await _context.Users.AnyAsync(u => u.Id == userId, ct))
                throw new UserNotFoundException(userId);

            var normalizedKind = NormalizeKind(kind);
            var contact = new UserContact
            {
                UserId    = userId,
                Kind      = normalizedKind,
                Label     = Clean(label),
                Value     = value.Trim(),
                IsPrimary = isPrimary,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _context.UserContacts.Add(contact);

            if (isPrimary) await DemoteOtherPrimariesAsync(userId, normalizedKind, contact.Id, ct);
            await _context.SaveChangesAsync(ct);
            return contact;
        }

        public async Task<UserContact> UpdateContactAsync(int userId, int contactId, string kind,
            string? label, string value, bool isPrimary, CancellationToken ct = default)
        {
            // Scoped by userId as well as id, so one user can never edit another's contacts by
            // guessing an id.
            var contact = await _context.UserContacts
                .FirstOrDefaultAsync(c => c.Id == contactId && c.UserId == userId, ct)
                ?? throw new UserContactNotFoundException(contactId);

            contact.Kind      = NormalizeKind(kind);
            contact.Label     = Clean(label);
            contact.Value     = value.Trim();
            contact.IsPrimary = isPrimary;
            contact.UpdatedAt = DateTime.UtcNow;

            if (isPrimary) await DemoteOtherPrimariesAsync(userId, contact.Kind, contactId, ct);
            await _context.SaveChangesAsync(ct);
            return contact;
        }

        public async Task DeleteContactAsync(int userId, int contactId, CancellationToken ct = default)
        {
            var contact = await _context.UserContacts
                .FirstOrDefaultAsync(c => c.Id == contactId && c.UserId == userId, ct)
                ?? throw new UserContactNotFoundException(contactId);

            _context.UserContacts.Remove(contact);
            await _context.SaveChangesAsync(ct);
        }

        /// <summary>At most one primary per (user, kind) — setting a new one clears the rest.</summary>
        private async Task DemoteOtherPrimariesAsync(int userId, string kind, int keepId, CancellationToken ct)
        {
            var others = await _context.UserContacts
                .Where(c => c.UserId == userId && c.Kind == kind && c.Id != keepId && c.IsPrimary)
                .ToListAsync(ct);
            foreach (var o in others) o.IsPrimary = false;
        }

        private static string NormalizeKind(string kind) => kind.Trim().ToLowerInvariant();

        private static string? Clean(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
