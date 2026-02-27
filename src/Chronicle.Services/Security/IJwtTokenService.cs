using System.Security.Claims;
using Chronicle.Core.Models;

namespace Chronicle.Services.Security
{
    public interface IJwtTokenService
    {
        string GenerateToken(User user);
        ClaimsPrincipal? ValidateToken(string token);
    }
}
