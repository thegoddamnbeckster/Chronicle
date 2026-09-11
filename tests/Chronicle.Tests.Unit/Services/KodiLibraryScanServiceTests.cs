using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class KodiLibraryScanServiceTests : IDisposable
{
    private readonly ChronicleDbContext _db;
    private readonly Mock<IKodiRpcClient> _rpcMock = new();
    private readonly KodiLibraryScanService _service;

    private const int MovieTypeId = 1;
    private const int TvTypeId    = 2;
    private const int MusicTypeId = 3;

    public KodiLibraryScanServiceTests()
    {
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ChronicleDbContext(options);

        _db.MediaTypes.Add(new MediaType { Id = MovieTypeId, Name = "movies", DisplayName = "Movies", IsActive = true });
        _db.MediaTypes.Add(new MediaType { Id = TvTypeId, Name = "tv", DisplayName = "TV", IsActive = true });
        _db.MediaTypes.Add(new MediaType { Id = MusicTypeId, Name = "music", DisplayName = "Music", IsActive = true });
        _db.Users.Add(new User { Id = 1, Username = "u", PasswordHash = "h", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        _rpcMock.Setup(r => r.ScanAsync(It.IsAny<KodiDevice>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var scopeFactory = BuildScopeFactory(_db, _rpcMock.Object);
        _service = new KodiLibraryScanService(scopeFactory, NullLogger<KodiLibraryScanService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<KodiDevice> RegisterDeviceAsync(int apiTokenId, string name, string host)
    {
        _db.ApiTokens.Add(new ApiToken
        {
            Id = apiTokenId, UserId = 1, Name = name, Token = "hashed" + apiTokenId,
            CreatedAt = DateTime.UtcNow, IsActive = true,
        });
        await _db.SaveChangesAsync();
        var device = new KodiDevice
        {
            UserId = 1, ApiTokenId = apiTokenId, Name = name, Host = host, Port = 8080,
            LastSeenAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
        };
        _db.KodiDevices.Add(device);
        await _db.SaveChangesAsync();
        return device;
    }

    [Fact]
    public async Task NotifyNewContentAsync_NonVideoLibraryType_NeverContactsAnyDevice()
    {
        // Music/audiobooks/people/etc. never reach a Kodi VideoLibrary at all -- triggering a
        // scan for these would just waste every device's time on a scan with nothing to find.
        await RegisterDeviceAsync(1, "Shield", "10.0.0.10");

        await _service.NotifyNewContentAsync(MusicTypeId);

        _rpcMock.Verify(r => r.ScanAsync(It.IsAny<KodiDevice>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotifyNewContentAsync_UnknownMediaTypeId_DoesNotThrow()
    {
        var act = async () => await _service.NotifyNewContentAsync(mediaTypeId: 999);
        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(MovieTypeId)]
    [InlineData(TvTypeId)]
    public async Task NotifyNewContentAsync_VideoLibraryType_TriggersScanOnRegisteredDevice(int mediaTypeId)
    {
        var device = await RegisterDeviceAsync(1, "Shield", "10.0.0.10");

        await _service.NotifyNewContentAsync(mediaTypeId);

        _rpcMock.Verify(r => r.ScanAsync(
            It.Is<KodiDevice>(d => d.Id == device.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyNewContentAsync_MultipleRegisteredDevices_TriggersEveryOne()
    {
        await RegisterDeviceAsync(1, "Shield Upstairs", "10.0.0.10");
        await RegisterDeviceAsync(2, "Shield Downstairs", "10.0.0.11");

        await _service.NotifyNewContentAsync(TvTypeId);

        _rpcMock.Verify(r => r.ScanAsync(It.IsAny<KodiDevice>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task NotifyNewContentAsync_NoRegisteredDevices_DoesNotThrow()
    {
        var act = async () => await _service.NotifyNewContentAsync(TvTypeId);

        await act.Should().NotThrowAsync();
        _rpcMock.Verify(r => r.ScanAsync(It.IsAny<KodiDevice>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotifyNewContentAsync_CalledTwiceInQuickSuccession_SecondCallIsThrottled()
    {
        // Prevents a burst of small back-to-back import batches (e.g. several scan folders
        // finishing within seconds of each other) from each triggering their own full
        // VideoLibrary.Scan on the same device.
        await RegisterDeviceAsync(1, "Shield", "10.0.0.10");

        await _service.NotifyNewContentAsync(TvTypeId);
        await _service.NotifyNewContentAsync(MovieTypeId);

        _rpcMock.Verify(r => r.ScanAsync(It.IsAny<KodiDevice>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyNewContentAsync_NewlyRegisteredDeviceAfterFirstCall_StillGetsItsOwnFirstScan()
    {
        // The throttle is keyed per-device -- a device that registers (or gets seen for the
        // first time) after an earlier notification must not be silently skipped just because
        // some OTHER device was already triggered recently.
        await RegisterDeviceAsync(1, "Shield", "10.0.0.10");
        await _service.NotifyNewContentAsync(TvTypeId);

        var device2 = await RegisterDeviceAsync(2, "Shield 2", "10.0.0.11");
        await _service.NotifyNewContentAsync(TvTypeId);

        _rpcMock.Verify(r => r.ScanAsync(
            It.Is<KodiDevice>(d => d.Id == device2.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyNewContentAsync_OneDeviceScanFails_StillReturnsWithoutThrowing()
    {
        await RegisterDeviceAsync(1, "Shield", "10.0.0.10");
        _rpcMock.Setup(r => r.ScanAsync(It.IsAny<KodiDevice>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var act = async () => await _service.NotifyNewContentAsync(TvTypeId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifyNewContentAsync_ExcludesDeviceWhoseApiTokenWasRevoked()
    {
        await RegisterDeviceAsync(1, "Shield", "10.0.0.10");
        var token = await _db.ApiTokens.FirstAsync(t => t.Id == 1);
        token.IsActive = false;
        await _db.SaveChangesAsync();

        await _service.NotifyNewContentAsync(TvTypeId);

        _rpcMock.Verify(r => r.ScanAsync(It.IsAny<KodiDevice>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static IServiceScopeFactory BuildScopeFactory(ChronicleDbContext db, IKodiRpcClient rpc)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(rpc);
        services.AddScoped<IKodiDeviceService, KodiDeviceService>();
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }
}
