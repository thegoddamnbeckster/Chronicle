using Chronicle.Core.Models;

namespace Chronicle.Services
{
    public interface IUserService
    {
        Task<User> AuthenticateAsync(string username, string password);
        Task<User> RegisterAsync(string username, string password, string? email);
        Task<User?> GetByIdAsync(int id);
        Task<User?> GetByUsernameAsync(string username);
    }
}
