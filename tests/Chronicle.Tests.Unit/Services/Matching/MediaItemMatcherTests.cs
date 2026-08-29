using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Matching;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services.Matching
{
    public class MediaItemMatcherTests : IDisposable
    {
        private readonly ChronicleDbContext _db;

        public MediaItemMatcherTests()
        {
            var options = new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            _db = new ChronicleDbContext(options);

            _db.MediaTypes.Add(new MediaType { Id = 1, Name = "tv", DisplayName = "TV Shows", CreatedAt = DateTime.UtcNow });
            _db.MediaTypes.Add(new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", CreatedAt = DateTime.UtcNow });
            _db.SaveChanges();
        }

        [Theory]
        [InlineData("movie", "movies")]
        [InlineData("film", "movies")]
        [InlineData("MOVIE", "movies")]
        [InlineData("tv_show", "tv")]
        [InlineData("tv_episode", "tv")]
        [InlineData("episode", "tv")] // Kodi's own Player.GetItem "type" value for TV episodes -- see Chronicle_Scrobbler's media_info.py
        [InlineData("track", "music")]
        [InlineData("book", "books")]
        [InlineData("audiobooks", "audiobooks")] // unrecognized -- passed through, not hardcoded away
        public void NormalizeMediaTypeName_MapsKnownAliasesToSeededNames(string input, string expected)
        {
            MediaItemMatcher.NormalizeMediaTypeName(input).Should().Be(expected);
        }

        [Fact]
        public async Task TryResolveMediaTypeIdForMatchAsync_MovieAlias_ResolvesToSeededMoviesRow()
        {
            // Regression test: ScrobbleService/ImportService used to normalize "movie"/"film"
            // to the string "movie" (singular), which never matched the actual seeded
            // MediaTypes.Name of "movies" (plural) -- this proves the fix resolves it.
            var id = await MediaItemMatcher.TryResolveMediaTypeIdForMatchAsync(_db, "movie", default);

            id.Should().Be(2);
        }

        [Fact]
        public async Task TryResolveMediaTypeIdForMatchAsync_UnrecognizedType_ReturnsNull()
        {
            var id = await MediaItemMatcher.TryResolveMediaTypeIdForMatchAsync(_db, "podcast", default);

            id.Should().BeNull();
        }

        [Fact]
        public async Task TryResolveMediaTypeIdForMatchAsync_BlankType_ReturnsNull()
        {
            var id = await MediaItemMatcher.TryResolveMediaTypeIdForMatchAsync(_db, null, default);

            id.Should().BeNull();
        }

        [Fact]
        public async Task FindByTitleYearAsync_ScopesToRequestedType_IgnoresSameNameOtherType()
        {
            _db.MediaItems.Add(new MediaItem
            {
                Id = 1, MediaTypeId = 1, Name = "Chronicle", Year = 2026,
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            _db.MediaItems.Add(new MediaItem
            {
                Id = 2, MediaTypeId = 2, Name = "Chronicle", Year = 2026,
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();

            var match = await MediaItemMatcher.FindByTitleYearAsync(_db, "Chronicle", 2026, mediaTypeId: 2, default);

            match.Should().NotBeNull();
            match!.Id.Should().Be(2);
        }

        [Fact]
        public async Task FindByTitleYearAsync_DashColonVariant_StillMatches()
        {
            _db.MediaItems.Add(new MediaItem
            {
                Id = 1, MediaTypeId = 2, Name = "A: B", Year = 2020,
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();

            var match = await MediaItemMatcher.FindByTitleYearAsync(_db, "A - B", 2020, mediaTypeId: 2, default);

            match.Should().NotBeNull();
            match!.Id.Should().Be(1);
        }

        [Fact]
        public async Task FindByTitleYearAsync_ExcludesCollectionContainer_EvenOnExactTitleMatch()
        {
            // Regression test (2026-08-29): a sync event titled "Robot Jox Collection" (Simkl
            // tracks "collections" as their own trackable entity) used to match straight onto
            // Chronicle's own movie-set CONTAINER of that exact name, silently setting a watch
            // status on something nobody ever actually watched -- confirmed live via zero
            // backing interaction_events. A container is never a valid match target for a
            // scrobble/import/sync event.
            _db.MediaItems.Add(new MediaItem
            {
                Id = 1, MediaTypeId = 2, Name = "Robot Jox Collection",
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            _db.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = 1, Source = "chronicle", ExternalId = "collection:1",
            });
            await _db.SaveChangesAsync();

            var match = await MediaItemMatcher.FindByTitleYearAsync(
                _db, "Robot Jox Collection", year: null, mediaTypeId: 2, default);

            match.Should().BeNull();
        }

        [Fact]
        public async Task FindByTitleYearAsync_StillMatchesTvShowWithSeasonChildren()
        {
            // Guards against a too-broad fix for the container-exclusion test above: a real
            // TV show's season/episode children are normal structure, not a sign of being a
            // synthetic container -- excluding "has children" here would have broken this.
            _db.MediaItems.Add(new MediaItem
            {
                Id = 1, MediaTypeId = 1, Name = "Rick and Morty", Year = 2013,
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            _db.MediaItems.Add(new MediaItem
            {
                Id = 2, MediaTypeId = 1, Name = "Season 01", ParentId = 1,
                HierarchyLevel = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();

            var match = await MediaItemMatcher.FindByTitleYearAsync(
                _db, "Rick and Morty", year: 2013, mediaTypeId: 1, default);

            match.Should().NotBeNull();
            match!.Id.Should().Be(1);
        }

        [Fact]
        public async Task ResolveMediaTypeIdForStubAsync_UnresolvedType_FallsBackToFirstActiveType()
        {
            var id = await MediaItemMatcher.ResolveMediaTypeIdForStubAsync(_db, "podcast", default);

            id.Should().Be(1); // "tv" -- lowest Id among active types
        }

        public void Dispose() => _db.Dispose();
    }
}
