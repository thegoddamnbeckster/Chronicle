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

        public void Dispose() => _context.Dispose();
    }
}
