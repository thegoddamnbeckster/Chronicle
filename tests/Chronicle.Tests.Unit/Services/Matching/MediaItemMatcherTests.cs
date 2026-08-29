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

        [Fact]
        public async Task FindEpisodeAsync_NoSeason_ReturnsNull()
        {
            var show = await AddShowAsync();

            var episode = await MediaItemMatcher.FindEpisodeAsync(_db, show.Id, season: 1, episode: 1, default);

            episode.Should().BeNull();
        }

        [Fact]
        public async Task FindEpisodeAsync_SeasonExistsButNotEpisode_ReturnsNull()
        {
            var show = await AddShowAsync();
            await AddSeasonAsync(show.Id, 1);

            var episode = await MediaItemMatcher.FindEpisodeAsync(_db, show.Id, season: 1, episode: 4, default);

            episode.Should().BeNull();
        }

        [Fact]
        public async Task FindEpisodeAsync_ExistingEpisode_ReturnsIt()
        {
            var show = await AddShowAsync();
            var season = await AddSeasonAsync(show.Id, 3);
            var episode = await AddEpisodeAsync(season.Id, 4, "Vindicators 3: The Return of Worldender");

            var match = await MediaItemMatcher.FindEpisodeAsync(_db, show.Id, season: 3, episode: 4, default);

            match.Should().NotBeNull();
            match!.Id.Should().Be(episode.Id);
        }

        [Fact]
        public async Task FindOrCreateEpisodeAsync_NoExistingSeasonOrEpisode_CreatesBoth()
        {
            var show = await AddShowAsync();

            var episode = await MediaItemMatcher.FindOrCreateEpisodeAsync(
                _db, show, season: 3, episode: 4, episodeTitle: "Vindicators 3: The Return of Worldender", default);

            episode.Name.Should().Be("Vindicators 3: The Return of Worldender");
            episode.HierarchyLevel.Should().Be(2);
            episode.Number.Should().Be(4);

            var season = await _db.MediaItems.SingleAsync(i => i.ParentId == show.Id && i.HierarchyLevel == 1);
            season.Number.Should().Be(3);
            episode.ParentId.Should().Be(season.Id);
        }

        [Fact]
        public async Task FindOrCreateEpisodeAsync_NoEpisodeTitleSupplied_FallsBackToSeasonEpisodeCode()
        {
            var show = await AddShowAsync();

            var episode = await MediaItemMatcher.FindOrCreateEpisodeAsync(
                _db, show, season: 3, episode: 4, episodeTitle: null, default);

            episode.Name.Should().Be("S03E04");
        }

        [Fact]
        public async Task FindOrCreateEpisodeAsync_ExistingEpisodeWithRealTitle_NeverOverwritesIt()
        {
            // An already-scraped episode (TMDB/TVDB/NFO import) has a real title -- a scrobble's
            // own episodeTitle (which may just be Kodi's locally cached label) must never clobber it.
            var show = await AddShowAsync();
            var season = await AddSeasonAsync(show.Id, 3);
            var existing = await AddEpisodeAsync(season.Id, 4, "Vindicators 3: The Return of Worldender");

            var episode = await MediaItemMatcher.FindOrCreateEpisodeAsync(
                _db, show, season: 3, episode: 4, episodeTitle: "Some Other Title", default);

            episode.Id.Should().Be(existing.Id);
            episode.Name.Should().Be("Vindicators 3: The Return of Worldender");
        }

        [Fact]
        public async Task FindOrCreateEpisodeAsync_ExistingEpisodeStillHasPlaceholderName_UpgradesToRealTitle()
        {
            // Regression test (2026-08-29, "you're missing the episode name"): an episode synced
            // in earlier (e.g. via Simkl import, which doesn't always carry a per-episode title)
            // can sit indefinitely with a generic "S03E04"-style Name even after metadata
            // enrichment succeeds elsewhere, since enrichment never writes back into the raw Name
            // column ActiveSessionDto/HistoryItemDto read directly. Confirmed live against a real
            // "Rick and Morty" S03E04 item. A scrobble carrying a real episodeTitle (Kodi's local
            // library scan almost always has one) should upgrade the stale placeholder.
            var show = await AddShowAsync();
            var season = await AddSeasonAsync(show.Id, 3);
            var existing = await AddEpisodeAsync(season.Id, 4, "S03E04");

            var episode = await MediaItemMatcher.FindOrCreateEpisodeAsync(
                _db, show, season: 3, episode: 4, episodeTitle: "Vindicators 3: The Return of Worldender", default);

            episode.Id.Should().Be(existing.Id);
            episode.Name.Should().Be("Vindicators 3: The Return of Worldender");
        }

        [Fact]
        public async Task FindOrCreateEpisodeAsync_ExistingPlaceholderNamedEpisode_NoEpisodeTitleSupplied_LeavesPlaceholderAlone()
        {
            var show = await AddShowAsync();
            var season = await AddSeasonAsync(show.Id, 3);
            var existing = await AddEpisodeAsync(season.Id, 4, "S03E04");

            var episode = await MediaItemMatcher.FindOrCreateEpisodeAsync(
                _db, show, season: 3, episode: 4, episodeTitle: null, default);

            episode.Id.Should().Be(existing.Id);
            episode.Name.Should().Be("S03E04");
        }

        private async Task<MediaItem> AddShowAsync()
        {
            var show = new MediaItem
            {
                MediaTypeId = 1, Name = "Rick and Morty", Year = 2013,
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _db.MediaItems.Add(show);
            await _db.SaveChangesAsync();
            return show;
        }

        private async Task<MediaItem> AddSeasonAsync(int showId, int number)
        {
            var season = new MediaItem
            {
                MediaTypeId = 1, Name = $"Season {number}", ParentId = showId,
                HierarchyLevel = 1, Number = number, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _db.MediaItems.Add(season);
            await _db.SaveChangesAsync();
            return season;
        }

        private async Task<MediaItem> AddEpisodeAsync(int seasonId, int number, string name)
        {
            var episode = new MediaItem
            {
                MediaTypeId = 1, Name = name, ParentId = seasonId,
                HierarchyLevel = 2, Number = number, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _db.MediaItems.Add(episode);
            await _db.SaveChangesAsync();
            return episode;
        }

        public void Dispose() => _db.Dispose();
    }
}
