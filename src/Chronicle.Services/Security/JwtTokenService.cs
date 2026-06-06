using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Chronicle.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Serilog;

namespace Chronicle.Services.Security
{
    public class JwtTokenService : IJwtTokenService
    {
        private readonly ILogger _log = Log.ForContext<JwtTokenService>();
        private readonly SymmetricSecurityKey _key;
        private readonly int _expirationHours;

        public JwtTokenService(IConfiguration configuration)
        {
            var secret = configuration["Security:JwtSecret"]
                ?? throw new InvalidOperationException("JWT secret not configured. Set Security:JwtSecret in appsettings.");
            if (secret.Length < 64)
                throw new InvalidOperationException("JWT secret must be at least 64 characters.");

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

            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                issuer: "Chronicle",
                audience: "Chronicle",
                claims: claims,
                notBefore: now,
                expires: now.AddHours(_expirationHours),
                signingCredentials: credentials
            );
            // Stamp iat (issued-at) manually — the JwtSecurityToken ctor doesn't expose it directly.
            token.Payload[JwtRegisteredClaimNames.Iat] = new DateTimeOffset(now).ToUnixTimeSeconds();

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
            catch (SecurityTokenExpiredException ex)
            {
                _log.Debug(ex, "JWT token rejected — expired");
                return null;
            }
            catch (SecurityTokenException ex)
            {
                // Covers invalid signature, malformed token, wrong issuer/audience, etc.
                _log.Warning(ex, "JWT token rejected — validation failure");
                return null;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "JWT token rejected — unexpected error during validation");
                return null;
            }
        }
    }
}
