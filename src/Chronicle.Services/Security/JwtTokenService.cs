using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Chronicle.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Chronicle.Services.Security
{
    public class JwtTokenService : IJwtTokenService
    {
        private readonly SymmetricSecurityKey _key;
        private readonly int _expirationHours;

        public JwtTokenService(IConfiguration configuration)
        {
            var secret = configuration["Security:JwtSecret"]
                ?? throw new InvalidOperationException("JWT secret not configured. Set Security:JwtSecret in appsettings.");
            if (secret.Length < 32)
                throw new InvalidOperationException("JWT secret must be at least 32 characters.");

            _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            _expirationHours = int.TryParse(configuration["Security:JwtExpirationHours"], out var h) ? h : 24;
        }

        public string GenerateToken(User user)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User")
            };

            var credentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: "Chronicle",
                audience: "Chronicle",
                claims: claims,
                expires: DateTime.UtcNow.AddHours(_expirationHours),
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public ClaimsPrincipal? ValidateToken(string token)
        {
            var handler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _key,
                ValidateIssuer = true,
                ValidIssuer = "Chronicle",
                ValidateAudience = true,
                ValidAudience = "Chronicle",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };

            try
            {
                return handler.ValidateToken(token, parameters, out _);
            }
            catch
            {
                return null;
            }
        }
    }
}
