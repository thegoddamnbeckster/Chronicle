using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services
{
    public class UserService : IUserService
    {
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
            await _context.SaveChangesAsync();

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
    }
}
