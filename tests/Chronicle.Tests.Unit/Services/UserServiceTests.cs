using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Chronicle.Tests.Unit.Services
{
    public class UserServiceTests : IDisposable
    {
        private readonly ChronicleDbContext _context;
        private readonly Mock<IPasswordHasher> _hasherMock;
        private readonly UserService _service;

        public UserServiceTests()
        {
            var options = new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            _context = new ChronicleDbContext(options);
            _hasherMock = new Mock<IPasswordHasher>();
            _hasherMock.Setup(h => h.HashPassword(It.IsAny<string>())).Returns("hashed");
            _hasherMock.Setup(h => h.VerifyPassword("correct", "hashed")).Returns(true);
            _hasherMock.Setup(h => h.VerifyPassword(It.Is<string>(p => p != "correct"), "hashed")).Returns(false);

            _service = new UserService(_context, _hasherMock.Object);
        }

        [Fact]
        public async Task RegisterAsync_NewUser_CreatesUser()
        {
            var user = await _service.RegisterAsync("alice", "correct", "alice@example.com");

            user.Should().NotBeNull();
            user.Username.Should().Be("alice");
            user.Email.Should().Be("alice@example.com");
        }

        [Fact]
        public async Task RegisterAsync_FirstUser_IsAdmin()
        {
            var user = await _service.RegisterAsync("alice", "correct", null);
            user.IsAdmin.Should().BeTrue();
        }

        [Fact]
        public async Task RegisterAsync_SecondUser_IsNotAdmin()
        {
            await _service.RegisterAsync("alice", "correct", null);
            var second = await _service.RegisterAsync("bob", "correct", null);
            second.IsAdmin.Should().BeFalse();
        }

        [Fact]
        public async Task RegisterAsync_DuplicateUsername_Throws()
        {
            await _service.RegisterAsync("alice", "correct", null);
            await FluentActions.Invoking(() => _service.RegisterAsync("alice", "correct", null))
                .Should().ThrowAsync<DuplicateUsernameException>();
        }

        [Fact]
        public async Task AuthenticateAsync_ValidCredentials_ReturnsUser()
        {
            await _service.RegisterAsync("alice", "correct", null);
            var user = await _service.AuthenticateAsync("alice", "correct");
            user.Should().NotBeNull();
            user.Username.Should().Be("alice");
        }

        [Fact]
        public async Task AuthenticateAsync_WrongPassword_Throws()
        {
            await _service.RegisterAsync("alice", "correct", null);
            await FluentActions.Invoking(() => _service.AuthenticateAsync("alice", "wrong"))
                .Should().ThrowAsync<InvalidCredentialsException>();
        }

        [Fact]
        public async Task AuthenticateAsync_UnknownUser_Throws()
        {
            await FluentActions.Invoking(() => _service.AuthenticateAsync("nobody", "correct"))
                .Should().ThrowAsync<InvalidCredentialsException>();
        }

        [Fact]
        public async Task GetByIdAsync_ExistingUser_ReturnsUser()
        {
            var created = await _service.RegisterAsync("alice", "correct", null);
            var fetched = await _service.GetByIdAsync(created.Id);
            fetched.Should().NotBeNull();
            fetched!.Username.Should().Be("alice");
        }

        [Fact]
        public async Task GetByIdAsync_MissingUser_ReturnsNull()
        {
            var result = await _service.GetByIdAsync(999);
            result.Should().BeNull();
        }

        public void Dispose() => _context.Dispose();
    }
}
