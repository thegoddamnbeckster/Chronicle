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

            // A scrobble at/above WatchedThreshold marks the item Completed outright, not
            // "Watching" -- confirmed live 2026-08-24 that the previous behavior (asserted
            // here until now) left watched-threshold scrobbles stuck as Watching/Unwatched
            // forever, since nothing ever transitioned a library entry to Completed.
            libraryEntry.Should().NotBeNull();
            libraryEntry!.Status.Should().Be(LibraryStatus.Completed);
            libraryEntry.CompletedAt.Should().NotBeNull();
            libraryEntry.ResumePositionPercent.Should().BeNull();
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
        public async Task ScrobbleAsync_NoMediaItemId_TitleMatchesAcrossTypes_ScopesMatchToRequestedMediaType()
        {
            // Seed a movie sharing the exact name of the TV item already seeded in the
            // constructor ("Test Episode", MediaTypeId 1/"tv") — the title fallback must not
            // cross media types just because the names happen to collide.
            _context.MediaTypes.Add(new MediaType
            {
                Id = 2, Name = "movies", DisplayName = "Movies", CreatedAt = DateTime.UtcNow
            });
            _context.MediaItems.Add(new MediaItem
            {
                Id = 2, MediaTypeId = 2, Name = "Test Episode", Year = 2020,
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            var request = new ScrobbleRequest(
                MediaItemId: null, ProgressPercent: 50.0, Timestamp: null, DeviceName: "Kodi",
                Title: "Test Episode", MediaType: "movie");

            var result = await _service.ScrobbleAsync(1, request);

            result.Event.MediaItemId.Should().Be(2); // the movie, not the TV item with the same name
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

        [Fact]
        public async Task ScrobbleAsync_BelowWatchedThreshold_SetsResumePosition()
        {
            var when = DateTime.UtcNow;
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 42.0, when, "Kodi"));

            var entry = await _context.UserLibraries.FirstAsync(l => l.UserId == 1 && l.MediaItemId == 1);
            entry.ResumePositionPercent.Should().Be(42.0);
            entry.ResumeUpdatedAt.Should().Be(when);
        }

        [Fact]
        public async Task ScrobbleAsync_LowProgress_StillCreatesLibraryEntryAsWatching()
        {
            // Unlike the old watched-threshold-only upsert, ANY scrobble should create/
            // update the library entry now -- resume position has to be tracked from the
            // very first progress update, not just once an item is finished.
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 5.0, null, "Kodi"));

            var entry = await _context.UserLibraries.FirstOrDefaultAsync(l => l.UserId == 1 && l.MediaItemId == 1);
            entry.Should().NotBeNull();
            entry!.Status.Should().Be(LibraryStatus.Watching);
        }

        [Fact]
        public async Task ScrobbleAsync_CrossesWatchedThreshold_ClearsResumePosition()
        {
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 60.0, DateTime.UtcNow.AddMinutes(-5), "Kodi"));
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 95.0, DateTime.UtcNow, "Kodi"));

            var entry = await _context.UserLibraries.FirstAsync(l => l.UserId == 1 && l.MediaItemId == 1);
            entry.ResumePositionPercent.Should().BeNull();
            entry.ResumeUpdatedAt.Should().BeNull();
        }

        [Fact]
        public async Task GetResumeStateAsync_KnownItemWithResumePosition_ReturnsIt()
        {
            var when = DateTime.UtcNow;
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 33.0, when, "Kodi"));

            var state = await _service.GetResumeStateAsync(1, new ResumeLookupRequest(1));

            state.Should().NotBeNull();
            state!.MediaItemId.Should().Be(1);
            state.ResumePositionPercent.Should().Be(33.0);
            state.ResumeUpdatedAt.Should().Be(when);
        }

        [Fact]
        public async Task GetResumeStateAsync_ItemNeverScrobbled_ReturnsNull()
        {
            var state = await _service.GetResumeStateAsync(1, new ResumeLookupRequest(1));

            state.Should().BeNull();
        }

        [Fact]
        public async Task GetResumeStateAsync_FullyWatchedItem_ReturnsNull()
        {
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 90.0, null, "Kodi"));

            var state = await _service.GetResumeStateAsync(1, new ResumeLookupRequest(1));

            state.Should().BeNull();
        }

        [Fact]
        public async Task GetResumeStateAsync_UnresolvableItem_ReturnsNullAndCreatesNoStub()
        {
            var before = await _context.MediaItems.CountAsync();

            var state = await _service.GetResumeStateAsync(1, new ResumeLookupRequest(
                MediaItemId: null, Title: "Something Chronicle Has Never Heard Of", Year: 2026, MediaType: "movie"));

            state.Should().BeNull();
            (await _context.MediaItems.CountAsync()).Should().Be(before); // never creates a stub
        }

        [Fact]
        public async Task GetResumeStateAsync_ResolvesByExternalId_LikeScrobbleDoes()
        {
            _context.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = 1, Source = "imdb", ExternalId = "tt1234567"
            });
            await _context.SaveChangesAsync();
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 20.0, null, "Kodi"));

            var state = await _service.GetResumeStateAsync(1, new ResumeLookupRequest(
                MediaItemId: null, ExternalIds: new Dictionary<string, string> { ["imdb"] = "tt1234567" }));

            state.Should().NotBeNull();
            state!.MediaItemId.Should().Be(1);
        }

        // ── RateAsync ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task RateAsync_KnownItem_SetsUserRating()
        {
            var result = await _service.RateAsync(1, new RateRequest(1, 8));

            result.MediaItemId.Should().Be(1);
            result.Rating.Should().Be(8);

            var entry = await _context.UserLibraries.FirstAsync(l => l.UserId == 1 && l.MediaItemId == 1);
            entry.UserRating.Should().Be(8);
        }

        [Fact]
        public async Task RateAsync_NoExistingLibraryEntry_CreatesOneAsCompleted()
        {
            // Rating something implies it was watched -- even if this user's own device
            // never itself scrobbled it (e.g. a different device did the scrobbling).
            await _service.RateAsync(1, new RateRequest(1, 9));

            var entry = await _context.UserLibraries.FirstOrDefaultAsync(l => l.UserId == 1 && l.MediaItemId == 1);
            entry.Should().NotBeNull();
            entry!.Status.Should().Be(LibraryStatus.Completed);
            entry.UserRating.Should().Be(9);
        }

        [Fact]
        public async Task RateAsync_ExistingLibraryEntry_OverwritesRatingWithoutChangingStatus()
        {
            await _service.ScrobbleAsync(1, new ScrobbleRequest(1, 40.0, null, "Kodi")); // -> Watching, unfinished

            await _service.RateAsync(1, new RateRequest(1, 7));

            var entry = await _context.UserLibraries.FirstAsync(l => l.UserId == 1 && l.MediaItemId == 1);
            entry.UserRating.Should().Be(7);
            entry.Status.Should().Be(LibraryStatus.Watching); // rating alone doesn't force it to Completed

            await _service.RateAsync(1, new RateRequest(1, 3)); // rate again -- overwrites, not additive
            entry.UserRating.Should().Be(3);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(11)]
        [InlineData(-1)]
        public async Task RateAsync_RatingOutsideOneToTen_ThrowsArgumentException(int rating)
        {
            await FluentActions.Invoking(() => _service.RateAsync(1, new RateRequest(1, rating)))
                .Should().ThrowAsync<ArgumentException>();
        }

        [Fact]
        public async Task RateAsync_UnknownMediaItemId_Throws()
        {
            await FluentActions.Invoking(() => _service.RateAsync(1, new RateRequest(999, 5)))
                .Should().ThrowAsync<MediaNotFoundException>();
        }

        [Fact]
        public async Task RateAsync_ResolvesByExternalId_LikeScrobbleDoes()
        {
            _context.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = 1, Source = "imdb", ExternalId = "tt1234567"
            });
            await _context.SaveChangesAsync();

            var result = await _service.RateAsync(1, new RateRequest(
                MediaItemId: null, Rating: 6,
                ExternalIds: new Dictionary<string, string> { ["imdb"] = "tt1234567" }));

            result.MediaItemId.Should().Be(1);
        }

        [Fact]
        public async Task RateAsync_UnresolvableTitle_ThrowsAndCreatesNoStub()
        {
            var before = await _context.MediaItems.CountAsync();

            await FluentActions.Invoking(() => _service.RateAsync(1, new RateRequest(
                    MediaItemId: null, Rating: 5,
                    Title: "Something Chronicle Has Never Heard Of", Year: 2026, MediaType: "movie")))
                .Should().ThrowAsync<MediaNotFoundException>();

            (await _context.MediaItems.CountAsync()).Should().Be(before); // never creates a stub
        }

        public void Dispose() => _context.Dispose();
    }
}
