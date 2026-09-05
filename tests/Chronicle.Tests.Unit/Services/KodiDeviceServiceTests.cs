using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services
{
    public class KodiDeviceServiceTests : IDisposable
    {
        private readonly ChronicleDbContext _context;
        private readonly KodiDeviceService _service;

        public KodiDeviceServiceTests()
        {
            var options = new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            _context = new ChronicleDbContext(options);
            _service = new KodiDeviceService(_context);

            _context.Users.Add(new User
            {
                Id = 1, Username = "testuser", PasswordHash = "h",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            _context.ApiTokens.Add(new ApiToken
            {
                Id = 1, UserId = 1, Name = "device", Token = "hashed",
                CreatedAt = DateTime.UtcNow, IsActive = true,
            });
            _context.MediaTypes.Add(new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", IsActive = true });
            _context.MediaItems.Add(new MediaItem
            {
                Id = 100, Name = "Test Movie", MediaTypeId = 1, HierarchyLevel = 0,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            _context.SaveChanges();
        }

        public void Dispose() => _context.Dispose();

        [Fact]
        public async Task RegisterAsync_CreatesOneDevicePerApiToken()
        {
            await _service.RegisterAsync(userId: 1, apiTokenId: 1, name: "Shield", host: "10.0.0.10", port: 8080,
                username: null, password: null);

            var devices = await _context.KodiDevices.ToListAsync();
            devices.Should().HaveCount(1);
            devices[0].Host.Should().Be("10.0.0.10");
            devices[0].Port.Should().Be(8080);
        }

        [Fact]
        public async Task RegisterAsync_CalledTwice_UpsertsTheSameRowInsteadOfDuplicating()
        {
            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.10", 8080, null, null);
            // Simulates a periodic re-registration heartbeat picking up a DHCP-renewed IP.
            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.99", 8080, "kodi", "secret");

            var devices = await _context.KodiDevices.ToListAsync();
            devices.Should().HaveCount(1, "re-registering the same ApiTokenId must update, not duplicate");
            devices[0].Host.Should().Be("10.0.0.99");
            devices[0].Username.Should().Be("kodi");
        }

        [Fact]
        public async Task RecordKodiIdAsync_WithNoRegisteredDevice_IsANoOp()
        {
            // Remote control off on this Kodi instance -- report_kodi_id fires anyway on every
            // ordinary scan; there's simply nothing to map it to yet.
            var act = async () => await _service.RecordKodiIdAsync(apiTokenId: 999, mediaItemId: 100, "movie", 42);

            await act.Should().NotThrowAsync();
            (await _context.KodiLibraryIds.ToListAsync()).Should().BeEmpty();
        }

        [Fact]
        public async Task RecordKodiIdAsync_CalledTwiceForSameItem_UpsertsInsteadOfDuplicating()
        {
            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.10", 8080, null, null);

            await _service.RecordKodiIdAsync(1, mediaItemId: 100, "movie", kodiId: 42);
            await _service.RecordKodiIdAsync(1, mediaItemId: 100, "movie", kodiId: 43); // Kodi reimported, new id

            var mappings = await _context.KodiLibraryIds.ToListAsync();
            mappings.Should().HaveCount(1);
            mappings[0].KodiId.Should().Be(43);
        }

        [Fact]
        public async Task GetPushTargetsAsync_ReturnsRegisteredDeviceForMappedItem()
        {
            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.10", 8080, null, null);
            await _service.RecordKodiIdAsync(1, mediaItemId: 100, "movie", kodiId: 42);

            var targets = await _service.GetPushTargetsAsync(mediaItemId: 100);

            targets.Should().HaveCount(1);
            targets[0].Device.Host.Should().Be("10.0.0.10");
            targets[0].Mapping.KodiId.Should().Be(42);
        }

        [Fact]
        public async Task GetPushTargetsAsync_ExcludesDeviceWhoseApiTokenWasRevoked()
        {
            // RevokeTokenAsync only flips IsActive -- it never deletes the ApiToken row, so
            // KodiDevice's cascade-on-delete FK never fires. GetPushTargetsAsync must filter
            // this itself or a revoked device keeps receiving pushes indefinitely.
            await _service.RegisterAsync(1, 1, "Shield", "10.0.0.10", 8080, null, null);
            await _service.RecordKodiIdAsync(1, mediaItemId: 100, "movie", kodiId: 42);

            var token = await _context.ApiTokens.FirstAsync(t => t.Id == 1);
            token.IsActive = false;
            await _context.SaveChangesAsync();

            var targets = await _service.GetPushTargetsAsync(mediaItemId: 100);

            targets.Should().BeEmpty();
        }

        [Fact]
        public async Task GetPushTargetsAsync_ForItemWithNoMapping_ReturnsEmpty()
        {
            var targets = await _service.GetPushTargetsAsync(mediaItemId: 100);
            targets.Should().BeEmpty();
        }
    }
}
