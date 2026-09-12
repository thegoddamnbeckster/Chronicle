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
        private readonly Mock<INfoRebuildQueueService> _rebuildQueueMock = new();
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
                _rebuildQueueMock.Object,
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
        public async Task TryPushAsync_SuccessfulWrite_CompletesTheItemsRebuildQueueRow()
        {
            // Root-caused live (2026-09-12): NfoGenerationService's own 2-minute scheduled sweep
            // and this live, event-driven push had no coordination -- during an active library
            // scan, a freshly-discovered item was simultaneously "pending" in the rebuild queue
            // AND being pushed live, so both independently rebuilt and wrote the identical NFO
            // within seconds of each other. Marking the item's own queue row(s) complete the
            // moment a live push actually succeeds is what prevents the next scheduled tick from
            // redundantly doing it again -- see CompleteForMediaItemAsync's own doc.
            var item = new MediaItem
            {
                Id = 220, Name = "Test Show 2", MediaTypeId = ShowTypeId, HierarchyLevel = 0,
                MetadataJson = FileScannerMetadataJson(_tempDir, null),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync();

            var service = BuildService("<tvshow><title>Test Show 2</title></tvshow>"u8.ToArray());
            var outcome = await service.TryPushAsync(mediaItemId: 220, userId: 1);

            outcome.Should().BeTrue();
            _rebuildQueueMock.Verify(q => q.CompleteForMediaItemAsync(220, It.IsAny<CancellationToken>()), Times.Once);
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
        public async Task PushAsync_DestinationFolderMissing_LogsButDoesNotRecordTaskFailure()
        {
            // Per-user correction (2026-09-11): "I don't want to see an unexplained error in
            // the UI. log it and move on." A write failure here is almost always environmental
            // (the item's drive is offline, a share dropped, the folder was deleted) rather than
            // a bug in the push itself -- there's nothing a user could act on from a permanent
            // red "FAILED" badge on the NFO Push card. This must be logged (still exercised via
            // the ILogger mock implicitly not throwing) but must NOT flip the shared
            // background_tasks row to a failed state.
            var missingDir = Path.Combine(_tempDir, "drive-not-mounted");
            var videoPath  = Path.Combine(missingDir, "Movie (2020).mkv");
            var item = new MediaItem
            {
                Id = 400, Name = "Movie", MediaTypeId = MovieTypeId, HierarchyLevel = 0,
                MetadataJson = FileScannerMetadataJson(missingDir, [videoPath]),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync();

            var service = BuildService("<movie><title>Movie</title></movie>"u8.ToArray());
            var act = async () => await service.PushAsync(mediaItemId: 400, userId: 1);

            await act.Should().NotThrowAsync();
            (await _context.BackgroundTasks.FindAsync("nfo-push")).Should().BeNull(
                "a per-item environmental write failure should not surface as a task-level FAILED badge");
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

        // ── TryPushAsync's own outcome contract ─────────────────────────────────
        // NfoGenerationService (the whole-backlog bulk generator) decides whether to mark a
        // queue row complete purely from this return value -- true/false/null need to mean
        // exactly what their own doc says, not just "PushAsync doesn't throw".

        [Fact]
        public async Task TryPushAsync_SuccessfulWrite_ReturnsTrue()
        {
            var item = new MediaItem
            {
                Id = 500, Name = "Outcome Movie", MediaTypeId = MovieTypeId, HierarchyLevel = 0,
                MetadataJson = FileScannerMetadataJson(_tempDir, [Path.Combine(_tempDir, "Outcome.mkv")]),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync();

            var service = BuildService("<movie/>"u8.ToArray());
            var outcome = await service.TryPushAsync(mediaItemId: 500, userId: 1);

            outcome.Should().BeTrue();
        }

        [Fact]
        public async Task TryPushAsync_UnknownMediaItem_ReturnsNull()
        {
            var service = BuildService([]);
            var outcome = await service.TryPushAsync(mediaItemId: 99999, userId: 1);
            outcome.Should().BeNull();
        }

        [Fact]
        public async Task TryPushAsync_NoFileScannerLocationYet_ReturnsNull()
        {
            var item = new MediaItem
            {
                Id = 501, Name = "Never Scanned Either", MediaTypeId = MovieTypeId, HierarchyLevel = 0,
                MetadataJson = null,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync();

            var service = BuildService([]);
            var outcome = await service.TryPushAsync(mediaItemId: 501, userId: 1);

            // Null (nothing to do yet), not false (an attempt was made and failed) -- the
            // distinction NfoGenerationService relies on to decide whether to keep retrying.
            outcome.Should().BeNull();
        }

        [Fact]
        public async Task TryPushAsync_SidecarEndpointReturnsError_ReturnsFalse()
        {
            var item = new MediaItem
            {
                Id = 502, Name = "Sidecar Failure", MediaTypeId = MovieTypeId, HierarchyLevel = 0,
                MetadataJson = FileScannerMetadataJson(_tempDir, [Path.Combine(_tempDir, "Failure.mkv")]),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync();

            var service = new NfoPushService(_context, _jwtMock.Object,
                new StubHttpClientFactory(new StubFailingSidecarHandler()),
                _rebuildQueueMock.Object,
                Mock.Of<ILogger<NfoPushService>>());
            var outcome = await service.TryPushAsync(mediaItemId: 502, userId: 1);

            // False (an attempt was made and failed), not null -- this IS a meaningful failure
            // NfoGenerationService should count and retry, not silently treat as "nothing to do".
            outcome.Should().BeFalse();
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

        /// <summary>Simulates the sidecar endpoint itself being unreachable/erroring -- the
        /// "false" branch of TryPushAsync's tri-state outcome, distinct from "nothing to push"
        /// (StubSidecarHandler covers that indirectly via a location-less item instead).</summary>
        private sealed class StubFailingSidecarHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
