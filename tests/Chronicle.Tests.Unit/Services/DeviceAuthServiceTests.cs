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
    public class DeviceAuthServiceTests : IDisposable
    {
        private readonly ChronicleDbContext   _context;
        private readonly Mock<IApiTokenService> _apiTokenMock;
        private readonly DeviceAuthService    _service;

        private const int UserId = 1;

        public DeviceAuthServiceTests()
        {
            var options = new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            _context      = new ChronicleDbContext(options);
            _apiTokenMock = new Mock<IApiTokenService>();
            _service      = new DeviceAuthService(_context, _apiTokenMock.Object);

            // Seed a user so FK constraints pass when ApproveAsync creates a token
            _context.Users.Add(new User
            {
                Id = UserId, Username = "testuser", PasswordHash = "h",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            _context.SaveChanges();

            // Default mock: CreateTokenAsync returns a valid token + raw key
            _apiTokenMock
                .Setup(s => s.CreateTokenAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int uid, string name, DateTime? exp, CancellationToken _) =>
                {
                    var token = new ApiToken
                    {
                        Id        = 1,
                        UserId    = uid,
                        Name      = name,
                        Token     = "hashed",
                        CreatedAt = DateTime.UtcNow
                    };
                    return (token, "chr_live_rawkey123");
                });
        }

        public void Dispose() => _context.Dispose();

        // ── Helpers ───────────────────────────────────────────────────────────

        private async Task<string> InitiateAsync(string? deviceName = null) =>
            (await _service.InitiateAsync(deviceName, "http://localhost")).Code;

        // ── InitiateAsync ─────────────────────────────────────────────────────

        [Fact]
        public async Task InitiateAsync_CreatesRecord_WithPendingStatus()
        {
            var result = await _service.InitiateAsync("Kodi", "http://localhost");

            result.Code.Should().NotBeNullOrEmpty();
            result.DisplayCode.Should().HaveLength(9);   // "XXXX-XXXX"
            result.ExpiresInSeconds.Should().Be(300);

            var record = await _context.DeviceAuthCodes
                .FirstOrDefaultAsync(c => c.Code == result.Code);
            record.Should().NotBeNull();
            record!.Status.Should().Be(DeviceAuthStatus.Pending);
            record.DeviceName.Should().Be("Kodi");
        }

        [Fact]
        public async Task InitiateAsync_GeneratesUniqueCodeEachTime()
        {
            var code1 = await InitiateAsync();
            var code2 = await InitiateAsync();
            code1.Should().NotBe(code2);
        }

        [Fact]
        public async Task InitiateAsync_NullDeviceName_StoredAsNull()
        {
            var code   = await InitiateAsync(null);
            var record = await _context.DeviceAuthCodes
                .FirstOrDefaultAsync(c => c.Code == code);
            record!.DeviceName.Should().BeNull();
        }

        [Fact]
        public async Task InitiateAsync_VerificationUrlContainsCode()
        {
            var result = await _service.InitiateAsync(null, "http://localhost:8080");
            result.VerificationUrl.Should().Contain(result.Code);
        }

        // ── GetInfoAsync ──────────────────────────────────────────────────────

        [Fact]
        public async Task GetInfoAsync_PendingCode_ReturnsPendingInfo()
        {
            var code   = await InitiateAsync("My Device");
            var result = await _service.GetInfoAsync(code);

            result.Should().NotBeNull();
            result!.Status.Should().Be("pending");
            result.DeviceName.Should().Be("My Device");
        }

        [Fact]
        public async Task GetInfoAsync_UnknownCode_ReturnsNull()
        {
            var result = await _service.GetInfoAsync("doesnotexist");
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetInfoAsync_ExpiredCode_ReturnsExpiredStatus()
        {
            var code   = await InitiateAsync();
            var record = await _context.DeviceAuthCodes
                .FirstAsync(c => c.Code == code);

            // Force it to be expired
            record.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await _context.SaveChangesAsync();

            var result = await _service.GetInfoAsync(code);
            result!.Status.Should().Be("expired");
        }

        // ── ApproveAsync ──────────────────────────────────────────────────────

        [Fact]
        public async Task ApproveAsync_PendingCode_SetsApprovedStatus()
        {
            var code = await InitiateAsync("TV");

            await _service.ApproveAsync(UserId, code);

            var record = await _context.DeviceAuthCodes
                .FirstAsync(c => c.Code == code);
            record.Status.Should().Be(DeviceAuthStatus.Approved);
            record.UserId.Should().Be(UserId);
            record.RawApiKey.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task ApproveAsync_PendingCode_CreatesApiToken()
        {
            var code = await InitiateAsync("Kodi");

            await _service.ApproveAsync(UserId, code);

            _apiTokenMock.Verify(s =>
                s.CreateTokenAsync(UserId, It.IsAny<string>(), null, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task ApproveAsync_UnknownCode_ThrowsDeviceAuthCodeNotFoundException()
        {
            var act = async () => await _service.ApproveAsync(UserId, "badcode");
            await act.Should().ThrowAsync<DeviceAuthCodeNotFoundException>();
        }

        [Fact]
        public async Task ApproveAsync_AlreadyApprovedCode_ThrowsDeviceAuthCodeAlreadyUsedException()
        {
            var code = await InitiateAsync();
            await _service.ApproveAsync(UserId, code);

            var act = async () => await _service.ApproveAsync(UserId, code);
            await act.Should().ThrowAsync<DeviceAuthCodeAlreadyUsedException>();
        }

        [Fact]
        public async Task ApproveAsync_ExpiredCode_ThrowsDeviceAuthCodeExpiredException()
        {
            var code   = await InitiateAsync();
            var record = await _context.DeviceAuthCodes.FirstAsync(c => c.Code == code);
            record.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await _context.SaveChangesAsync();

            var act = async () => await _service.ApproveAsync(UserId, code);
            await act.Should().ThrowAsync<DeviceAuthCodeExpiredException>();
        }

        // ── DenyAsync ─────────────────────────────────────────────────────────

        [Fact]
        public async Task DenyAsync_PendingCode_SetsDeniedStatus()
        {
            var code = await InitiateAsync();
            await _service.DenyAsync(UserId, code);

            var record = await _context.DeviceAuthCodes
                .FirstAsync(c => c.Code == code);
            record.Status.Should().Be(DeviceAuthStatus.Denied);
        }

        [Fact]
        public async Task DenyAsync_UnknownCode_ThrowsDeviceAuthCodeNotFoundException()
        {
            var act = async () => await _service.DenyAsync(UserId, "nosuchcode");
            await act.Should().ThrowAsync<DeviceAuthCodeNotFoundException>();
        }

        [Fact]
        public async Task DenyAsync_AlreadyDeniedCode_ThrowsDeviceAuthCodeAlreadyUsedException()
        {
            var code = await InitiateAsync();
            await _service.DenyAsync(UserId, code);

            var act = async () => await _service.DenyAsync(UserId, code);
            await act.Should().ThrowAsync<DeviceAuthCodeAlreadyUsedException>();
        }

        // ── PollAsync ─────────────────────────────────────────────────────────

        [Fact]
        public async Task PollAsync_PendingCode_ReturnsPending()
        {
            var code   = await InitiateAsync();
            var result = await _service.PollAsync(code);

            result.Status.Should().Be("pending");
            result.ApiKey.Should().BeNull();
        }

        [Fact]
        public async Task PollAsync_ApprovedCode_ReturnsApprovedWithKey()
        {
            var code = await InitiateAsync();
            await _service.ApproveAsync(UserId, code);

            var result = await _service.PollAsync(code);

            result.Status.Should().Be("approved");
            result.ApiKey.Should().Be("chr_live_rawkey123");
        }

        [Fact]
        public async Task PollAsync_ApprovedCode_SecondPoll_ReturnsApprovedWithoutKey()
        {
            var code = await InitiateAsync();
            await _service.ApproveAsync(UserId, code);

            await _service.PollAsync(code);      // First poll — key returned + Retrieved transition
            var second = await _service.PollAsync(code);   // Second poll

            second.Status.Should().Be("approved");
            second.ApiKey.Should().BeNull();
        }

        [Fact]
        public async Task PollAsync_DeniedCode_ReturnsDenied()
        {
            var code = await InitiateAsync();
            await _service.DenyAsync(UserId, code);

            var result = await _service.PollAsync(code);
            result.Status.Should().Be("denied");
        }

        [Fact]
        public async Task PollAsync_UnknownCode_ReturnsExpired()
        {
            var result = await _service.PollAsync("nonexistent");
            result.Status.Should().Be("expired");
        }

        [Fact]
        public async Task PollAsync_ExpiredCode_ReturnsExpired()
        {
            var code   = await InitiateAsync();
            var record = await _context.DeviceAuthCodes.FirstAsync(c => c.Code == code);
            record.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await _context.SaveChangesAsync();

            var result = await _service.PollAsync(code);
            result.Status.Should().Be("expired");
        }

        // ── GetVerificationUrlAsync ───────────────────────────────────────────

        [Fact]
        public async Task GetVerificationUrlAsync_ExistingCode_ReturnsUrl()
        {
            var code = await InitiateAsync();
            var url  = await _service.GetVerificationUrlAsync(code, "http://localhost:8080");

            url.Should().NotBeNull();
            url.Should().Contain(code);
        }

        [Fact]
        public async Task GetVerificationUrlAsync_UnknownCode_ReturnsNull()
        {
            var url = await _service.GetVerificationUrlAsync("nosuchcode", "http://localhost");
            url.Should().BeNull();
        }

        // ── CleanupExpiredAsync ───────────────────────────────────────────────

        [Fact]
        public async Task CleanupExpiredAsync_RemovesOldRecords()
        {
            // Initiate and manually age the record to >24h ago
            var code   = await InitiateAsync();
            var record = await _context.DeviceAuthCodes.FirstAsync(c => c.Code == code);
            record.ExpiresAt = DateTime.UtcNow.AddHours(-25);
            await _context.SaveChangesAsync();

            await _service.CleanupExpiredAsync();

            var stored = await _context.DeviceAuthCodes.FindAsync(record.Id);
            stored.Should().BeNull();
        }

        [Fact]
        public async Task CleanupExpiredAsync_KeepsRecentRecords()
        {
            var code = await InitiateAsync();

            await _service.CleanupExpiredAsync();

            var stored = await _context.DeviceAuthCodes
                .FirstOrDefaultAsync(c => c.Code == code);
            stored.Should().NotBeNull();
        }
    }
}
