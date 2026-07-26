using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services
{
    public class ScrobbleServiceTests : IDisposable
    {
        private readonly ChronicleDbContext _context;
        private readonly ScrobbleService _service;

        public ScrobbleServiceTests()
        {
            var options = new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            _context = new ChronicleDbContext(options);
            _service = new ScrobbleService(_context);

            // Seed required data (EF 9 InMemory validates FK constraints)
            _context.Users.Add(new User
            {
                Id = 1, Username = "testuser", PasswordHash = "hash",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            _context.MediaTypes.Add(new MediaType
            {
                Id = 1, Name = "tv", DisplayName = "TV Shows",
                CreatedAt = DateTime.UtcNow
            });
            _context.MediaItems.Add(new MediaItem
            {
                Id = 1, MediaTypeId = 1, Name = "Test Episode",
                RuntimeMinutes = 45, HierarchyLevel = 2,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            _context.SaveChanges();
        }

        [Fact]
        public async Task ScrobbleAsync_ValidRequest_CreatesEvent()
        {
            var result = await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 50.0, null, "Kodi"));

            result.Should().NotBeNull();
            result.Event.UserId.Should().Be(1);
            result.Event.MediaItemId.Should().Be(1);
            result.Event.ProgressPercent.Should().Be(50.0);
        }

        [Fact]
        public async Task ScrobbleAsync_ProgressOver80_MarksAsWatched()
        {
            var result = await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 85.0, null, null));

            result.MarkedAsWatched.Should().BeTrue();
            result.Event.MarkedAsWatched.Should().BeTrue();
        }

        [Fact]
        public async Task ScrobbleAsync_ProgressUnder80_NotMarkedAsWatched()
        {
            var result = await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 79.9, null, null));

            result.MarkedAsWatched.Should().BeFalse();
        }

        [Fact]
        public async Task ScrobbleAsync_InvalidMediaId_Throws()
        {
            await FluentActions.Invoking(() => _service.ScrobbleAsync(1, new ScrobbleRequest(999, 50.0, null, null)))
                .Should().ThrowAsync<MediaNotFoundException>();
        }

        [Fact]
        public async Task ScrobbleAsync_WatchedScrobble_CreatesLibraryEntry()
        {
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 90.0, null, null));

            var libraryEntry = _context.UserLibraries
                .FirstOrDefault(l => l.UserId == 1 && l.MediaItemId == 1);

            libraryEntry.Should().NotBeNull();
            libraryEntry!.Status.Should().Be(LibraryStatus.Watching);
        }

        [Fact]
        public async Task GetHistoryAsync_ReturnsEventsInDescendingOrder()
        {
            var past = DateTime.UtcNow.AddHours(-1);
            var now = DateTime.UtcNow;

            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 50.0, past, null));
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 90.0, now, null));

            var history = (await _service.GetHistoryAsync(1)).ToList();

            history.Should().HaveCount(2);
            history[0].Timestamp.Should().BeAfter(history[1].Timestamp);
        }

        [Fact]
        public async Task ScrobbleAsync_NoMediaItemId_MatchesExistingItemByExternalId()
        {
            _context.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = 1, Source = "imdb", ExternalId = "tt1234567"
            });
            await _context.SaveChangesAsync();

            var request = new ScrobbleRequest(
                MediaItemId: null, ProgressPercent: 50.0, Timestamp: null, DeviceName: "Kodi",
                ExternalIds: new Dictionary<string, string> { ["imdb"] = "tt1234567" });

            var result = await _service.ScrobbleAsync(1, request);

            result.Event.MediaItemId.Should().Be(1);
            _context.MediaItems.Count().Should().Be(1); // no stub created — matched existing
        }

        [Fact]
        public async Task ScrobbleAsync_NoMediaItemId_NoMatch_CreatesStubItem()
        {
            var request = new ScrobbleRequest(
                MediaItemId: null, ProgressPercent: 10.0, Timestamp: null, DeviceName: "Kodi",
                ExternalIds: new Dictionary<string, string> { ["imdb"] = "tt9999999" },
                Title: "Brand New Movie", Year: 2026, MediaType: "movie");

            var result = await _service.ScrobbleAsync(1, request);

            result.Event.MediaItemId.Should().NotBe(1); // a new stub, not the seeded item
            var stub = await _context.MediaItems.FindAsync(result.Event.MediaItemId);
            stub!.Name.Should().Be("Brand New Movie");
            stub.Year.Should().Be(2026);

            var storedId = await _context.MediaExternalIds.FirstOrDefaultAsync(
                x => x.MediaItemId == stub.Id && x.Source == "imdb");
            storedId.Should().NotBeNull();
            storedId!.ExternalId.Should().Be("tt9999999");
        }

        [Fact]
        public async Task ScrobbleAsync_NoMediaItemId_NoTitleNoMatch_ThrowsArgumentException()
        {
            var request = new ScrobbleRequest(
                MediaItemId: null, ProgressPercent: 10.0, Timestamp: null, DeviceName: "Kodi");

            await FluentActions.Invoking(() => _service.ScrobbleAsync(1, request))
                .Should().ThrowAsync<ArgumentException>();
        }

        [Fact]
        public async Task GetWatchSummaryAsync_NoEvents_ReturnsZeroCountAndNullTimestamp()
        {
            var (lastWatchedAt, count) = await _service.GetWatchSummaryAsync(1, 1);

            count.Should().Be(0);
            lastWatchedAt.Should().BeNull();
        }

        [Fact]
        public async Task GetWatchSummaryAsync_CountsOnlyMarkedAsWatchedEvents_ForThatItem()
        {
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 90.0, DateTime.UtcNow.AddDays(-2), null)); // watched
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 30.0, DateTime.UtcNow.AddDays(-1), null)); // not watched
            var latest = DateTime.UtcNow;
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 95.0, latest, null));                      // watched

            var (lastWatchedAt, count) = await _service.GetWatchSummaryAsync(1, 1);

            count.Should().Be(2);
            lastWatchedAt.Should().BeCloseTo(latest, TimeSpan.FromSeconds(1));
        }

        public void Dispose() => _context.Dispose();
    }
}
