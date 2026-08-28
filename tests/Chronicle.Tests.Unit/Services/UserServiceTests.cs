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

            _service = new UserService(_context, _hasherMock.Object, new DeactivatedUserCache());
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

        [Fact]
        public async Task UpdatePreferencesAsync_FoldsMerges_DoesNotReplaceExistingKeys()
        {
            // Arrange — seed a user, set initial folds
            var user = await _service.RegisterAsync("alice", "correct", null);

            await _service.UpdatePreferencesAsync(user.Id, new UserPreferences
            {
                Folds = new() { { "media.1.tmdb", true } }
            });

            // Act — update a different key
            await _service.UpdatePreferencesAsync(user.Id, new UserPreferences
            {
                Folds = new() { { "media.2.tmdb", false } }
            });

            // Assert — both keys present with correct values
            var prefs = await _service.GetPreferencesAsync(user.Id);
            prefs.Folds.Should().ContainKey("media.1.tmdb");
            prefs.Folds.Should().ContainKey("media.2.tmdb");
            prefs.Folds!["media.1.tmdb"].Should().BeTrue();
            prefs.Folds["media.2.tmdb"].Should().BeFalse();
        }

        [Fact]
        public async Task UpdatePreferencesAsync_DefaultFoldsOpen_NullPatchDoesNotOverwrite()
        {
            // Arrange — set DefaultFoldsOpen to true
            var user = await _service.RegisterAsync("bob", "correct", null);
            await _service.UpdatePreferencesAsync(user.Id, new UserPreferences { DefaultFoldsOpen = true });

            // Act — patch with null (no value for DefaultFoldsOpen)
            await _service.UpdatePreferencesAsync(user.Id, new UserPreferences { DefaultFoldsOpen = null });

            // Assert — original value preserved
            var prefs = await _service.GetPreferencesAsync(user.Id);
            prefs.DefaultFoldsOpen.Should().BeTrue();
        }

        [Fact]
        public async Task UpdatePreferencesAsync_CreateCollectionStubs_Persists()
        {
            // Arrange
            var user = await _service.RegisterAsync("carol", "correct", null);

            // Act
            await _service.UpdatePreferencesAsync(user.Id, new UserPreferences { CreateCollectionStubs = false });

            // Assert
            var prefs = await _service.GetPreferencesAsync(user.Id);
            prefs.CreateCollectionStubs.Should().BeFalse();
        }

        [Fact]
        public async Task UpdatePreferencesAsync_ShowNowPlayingBanner_Persists()
        {
            // Arrange
            var user = await _service.RegisterAsync("dave", "correct", null);

            // Act
            await _service.UpdatePreferencesAsync(user.Id, new UserPreferences { ShowNowPlayingBanner = false });

            // Assert
            var prefs = await _service.GetPreferencesAsync(user.Id);
            prefs.ShowNowPlayingBanner.Should().BeFalse();
        }

        [Fact]
        public async Task GetPreferencesAsync_ShowNowPlayingBanner_UnsetByDefault()
        {
            // The "default to on" behavior lives in the API layer (UsersController's
            // `prefs.ShowNowPlayingBanner ?? true`), not here — an unset preference is null,
            // not true, at the service/storage layer. Pinning that distinction so a future
            // change doesn't accidentally bake the default into storage instead.
            var user = await _service.RegisterAsync("erin", "correct", null);

            var prefs = await _service.GetPreferencesAsync(user.Id);

            prefs.ShowNowPlayingBanner.Should().BeNull();
        }

        public void Dispose() => _context.Dispose();
    }
}
