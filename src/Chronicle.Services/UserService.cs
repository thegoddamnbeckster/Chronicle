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

        public UserService(ChronicleDbContext context, IPasswordHasher passwordHasher)
        {
            _context = context;
            _passwordHasher = passwordHasher;
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

            if (patch.DefaultFoldsOpen.HasValue)
                current.DefaultFoldsOpen = patch.DefaultFoldsOpen;

            if (patch.Folds is { Count: > 0 })
            {
                current.Folds ??= new Dictionary<string, bool>();
                foreach (var (key, value) in patch.Folds)
                    current.Folds[key] = value;
            }

            user.PreferencesJson = JsonSerializer.Serialize(current);
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }
    }
}
