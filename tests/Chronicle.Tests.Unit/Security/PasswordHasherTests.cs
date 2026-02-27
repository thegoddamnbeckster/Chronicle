using Chronicle.Services.Security;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Security
{
    public class PasswordHasherTests
    {
        private readonly PasswordHasher _hasher = new();

        [Fact]
        public void HashPassword_ReturnsNonEmptyHash()
        {
            var hash = _hasher.HashPassword("MyPassword123");
            hash.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void HashPassword_TwoCallsProduceDifferentHashes()
        {
            var hash1 = _hasher.HashPassword("MyPassword123");
            var hash2 = _hasher.HashPassword("MyPassword123");
            hash1.Should().NotBe(hash2); // BCrypt uses random salt
        }

        [Fact]
        public void VerifyPassword_CorrectPassword_ReturnsTrue()
        {
            var hash = _hasher.HashPassword("correct-password");
            _hasher.VerifyPassword("correct-password", hash).Should().BeTrue();
        }

        [Fact]
        public void VerifyPassword_WrongPassword_ReturnsFalse()
        {
            var hash = _hasher.HashPassword("correct-password");
            _hasher.VerifyPassword("wrong-password", hash).Should().BeFalse();
        }

        [Fact]
        public void VerifyPassword_EmptyPassword_ReturnsFalse()
        {
            var hash = _hasher.HashPassword("correct-password");
            _hasher.VerifyPassword("", hash).Should().BeFalse();
        }
    }
}
