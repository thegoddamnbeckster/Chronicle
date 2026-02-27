using Chronicle.Core.Models;
using Chronicle.Services.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace Chronicle.Tests.Unit.Security
{
    public class JwtTokenServiceTests
    {
        private readonly JwtTokenService _service;

        public JwtTokenServiceTests()
        {
            var source = new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?>
                {
                    ["Security:JwtSecret"] = "test-secret-must-be-at-least-32-characters-long",
                    ["Security:JwtExpirationHours"] = "24"
                }
            };
            var config = new ConfigurationBuilder().Add(source).Build();

            _service = new JwtTokenService(config);
        }

        private static User MakeUser(int id = 1, string username = "testuser", bool isAdmin = false) =>
            new()
            {
                Id = id,
                Username = username,
                PasswordHash = "hash",
                IsAdmin = isAdmin,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

        [Fact]
        public void GenerateToken_ReturnsNonEmptyString()
        {
            var token = _service.GenerateToken(MakeUser());
            token.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void ValidateToken_ValidToken_ReturnsPrincipal()
        {
            var token = _service.GenerateToken(MakeUser());
            var principal = _service.ValidateToken(token);
            principal.Should().NotBeNull();
        }

        [Fact]
        public void ValidateToken_InvalidToken_ReturnsNull()
        {
            var result = _service.ValidateToken("not.a.real.token");
            result.Should().BeNull();
        }

        [Fact]
        public void ValidateToken_ContainsCorrectUserId()
        {
            var user = MakeUser(id: 42);
            var token = _service.GenerateToken(user);
            var principal = _service.ValidateToken(token);

            var nameId = principal!.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            nameId.Should().Be("42");
        }

        [Fact]
        public void ValidateToken_AdminUser_HasAdminRole()
        {
            var user = MakeUser(isAdmin: true);
            var token = _service.GenerateToken(user);
            var principal = _service.ValidateToken(token);

            var role = principal!.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
            role.Should().Be("Admin");
        }
    }
}
