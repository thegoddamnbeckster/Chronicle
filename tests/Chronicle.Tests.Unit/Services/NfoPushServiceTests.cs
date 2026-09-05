using System.Net;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Chronicle.Tests.Unit.Services
{
    public class NfoPushServiceTests : IDisposable
    {
        private readonly ChronicleDbContext _context;
        private readonly Mock<IJwtTokenService> _jwtMock = new();
        private readonly Mock<IKodiDeviceService> _devicesMock = new();
        private readonly Mock<IKodiRpcClient> _rpcMock = new();
        private readonly string _tempDir;

        private const int MovieTypeId = 1;
        private const int ShowTypeId = 2;

        public NfoPushServiceTests()
        {
            var options = new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            _context = new ChronicleDbContext(options);

            _context.Users.Add(new User
            {
                Id = 1, Username = "testuser", PasswordHash = "h",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            _context.MediaTypes.Add(new MediaType { Id = MovieTypeId, Name = "movies", DisplayName = "Movies", IsActive = true });
            _context.MediaTypes.Add(new MediaType { Id = ShowTypeId, Name = "tv", DisplayName = "TV", IsActive = true });
            _context.SaveChanges();

            _jwtMock.Setup(j => j.GenerateToken(It.IsAny<User>())).Returns("fake-jwt");
            _devicesMock.Setup(d => d.GetPushTargetsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            _tempDir = Path.Combine(Path.GetTempPath(), "chronicle-nfo-push-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            _context.Dispose();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }

        private NfoPushService BuildService(byte[] sidecarResponseBytes) =>
            new(_context, _jwtMock.Object,
                new StubHttpClientFactory(new StubSidecarHandler(sidecarResponseBytes)),
                _devicesMock.Object, _rpcMock.Object,
                Mock.Of<ILogger<NfoPushService>>());

        private static string FileScannerMetadataJson(string? folderPath, string[]? filePaths, string? nfoPath = null) =>
            JsonSerializer.Serialize(new
            {
                fileScanner = new { folderPath, filePaths, nfoPath },
            });

        [Fact]
        public async Task PushAsync_MovieThatBelongsToACollection_StillWritesNfo()
        {
            // Regression test for the real bug this feature shipped with: a movie belonging to
            // a collection sits at HierarchyLevel 1 (the collection container itself is level 0),
            // which an earlier version of this classification incorrectly excluded -- confirmed
            // live against F9, a real member of "The Fast and the Furious Collection", which
            // silently no-opped instead of pushing. This item reproduces exactly that shape.
            var videoPath = Path.Combine(_tempDir, "F9 (2021).mkv");
            var item = new MediaItem
            {
                Id = 100, Name = "F9", MediaTypeId = MovieTypeId, HierarchyLevel = 1, ParentId = 999,
                MetadataJson = FileScannerMetadataJson(_tempDir, [videoPath]),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync();

            var service = BuildService("<movie><title>F9</title></movie>"u8.ToArray());
            await service.PushAsync(mediaItemId: 100, userId: 1);

            var expectedNfoPath = Path.Combine(_tempDir, "F9 (2021).nfo");
            File.Exists(expectedNfoPath).Should().BeTrue("a collection member is still a pushable movie");
            (await File.ReadAllTextAsync(expectedNfoPath)).Should().Contain("F9");
        }

        [Fact]
        public async Task PushAsync_TopLevelShow_WritesTvshowNfo()
        {
            var item = new MediaItem
            {
                Id = 200, Name = "Test Show", MediaTypeId = ShowTypeId, HierarchyLevel = 0,
                MetadataJson = FileScannerMetadataJson(_tempDir, null),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync();

            var service = BuildService("<tvshow><title>Test Show</title></tvshow>"u8.ToArray());
            await service.PushAsync(mediaItemId: 200, userId: 1);

            File.Exists(Path.Combine(_tempDir, "tvshow.nfo")).Should().BeTrue();
        }

        [Fact]
        public async Task PushAsync_SeasonLevelContainer_IsNotPushable()
        {
            // HierarchyLevel 1 under a show is a season -- unlike the movie/collection case
            // above, Kodi has no season-level NFO convention, so this must stay excluded.
            var item = new MediaItem
            {
                Id = 210, Name = "Season 1", MediaTypeId = ShowTypeId, HierarchyLevel = 1, ParentId = 200,
                MetadataJson = FileScannerMetadataJson(_tempDir, null),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync();

            var service = BuildService("<tvshow/>"u8.ToArray());
            await service.PushAsync(mediaItemId: 210, userId: 1);

            Directory.GetFiles(_tempDir).Should().BeEmpty("a season container has no NFO convention of its own");
        }

        [Fact]
        public async Task PushAsync_ItemWithNoFileScannerLocation_DoesNotThrowAndWritesNothing()
        {
            var item = new MediaItem
            {
                Id = 300, Name = "Never Scanned", MediaTypeId = MovieTypeId, HierarchyLevel = 0,
                MetadataJson = null,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync();

            var service = BuildService([]);
            var act = async () => await service.PushAsync(mediaItemId: 300, userId: 1);

            await act.Should().NotThrowAsync();
            Directory.GetFiles(_tempDir).Should().BeEmpty();
        }

        [Fact]
        public async Task PushAsync_UnknownMediaItem_DoesNotThrow()
        {
            var service = BuildService([]);
            var act = async () => await service.PushAsync(mediaItemId: 99999, userId: 1);
            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task PushAsync_WithRegisteredDevice_CallsRpcRefresh()
        {
            var item = new MediaItem
            {
                Id = 400, Name = "Pushable Movie", MediaTypeId = MovieTypeId, HierarchyLevel = 0,
                MetadataJson = FileScannerMetadataJson(_tempDir, [Path.Combine(_tempDir, "Movie.mkv")]),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync();

            var device = new KodiDevice { Id = 1, Name = "Shield", Host = "10.0.0.10", Port = 8080 };
            var mapping = new KodiLibraryId { KodiDeviceId = 1, MediaItemId = 400, Kind = "movie", KodiId = 42 };
            _devicesMock.Setup(d => d.GetPushTargetsAsync(400, It.IsAny<CancellationToken>()))
                .ReturnsAsync([(device, mapping)]);
            _rpcMock.Setup(r => r.RefreshAsync(device, "movie", 42, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var service = BuildService("<movie/>"u8.ToArray());
            await service.PushAsync(mediaItemId: 400, userId: 1);

            _rpcMock.Verify(r => r.RefreshAsync(device, "movie", 42, It.IsAny<CancellationToken>()), Times.Once);
        }

        // ── Test doubles ──────────────────────────────────────────────────────

        private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
        }

        /// <summary>Stands in for Chronicle's own sidecar endpoints -- returns fixed bytes
        /// regardless of which sidecar path was requested, so tests assert on the file this
        /// service writes, not on re-implementing ScraperController.</summary>
        private sealed class StubSidecarHandler(byte[] responseBytes) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(responseBytes) });
        }
    }
}
