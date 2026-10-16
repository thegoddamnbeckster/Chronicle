using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Matching;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services;

/// <summary>A title (and year) names a root item: the show, the film, the artist. Never an episode or a track that shares the name.</summary>
public class MediaItemMatcherRootTests
{
    private const int TvType = 1;
    private const int MovieType = 2;

    private static ChronicleDbContext Db()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = TvType, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.MediaTypes.Add(new MediaType { Id = MovieType, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return db;
    }

    private static MediaItem Item(int type, string name, int level, int? parent = null, int? year = null) => new()
    {
        MediaTypeId = type, Name = name, HierarchyLevel = level, ParentId = parent, Year = year,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task TheShowIsFound_NotAnEpisodeThatSharesItsName()
    {
        await using var db = Db();
        // The episode is the OLDER row (lower id), so an unscoped lookup would return it first.
        var episode = Item(TvType, "Rush", 2, parent: 2);
        db.MediaItems.Add(episode);
        await db.SaveChangesAsync();
        var show = Item(TvType, "Rush", 0);
        db.MediaItems.Add(show);
        await db.SaveChangesAsync();
        episode.Id.Should().BeLessThan(show.Id);

        (await MediaItemMatcher.FindByTitleYearAsync(db, "Rush", null, TvType, CancellationToken.None))!.Id.Should().Be(show.Id);
    }

    [Fact]
    public async Task OnlyAnEpisodeCarriesTheName_MeansNoMatch()
    {
        await using var db = Db();
        var show = Item(TvType, "Some Show", 0);
        db.MediaItems.Add(show);
        await db.SaveChangesAsync();
        db.MediaItems.Add(Item(TvType, "Rush", 2, show.Id));
        await db.SaveChangesAsync();

        (await MediaItemMatcher.FindByTitleYearAsync(db, "Rush", null, TvType, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AMovieInsideACollection_StillMatches_BecauseAFlatTypeCountsLevelOneAsRoot()
    {
        await using var db = Db();
        var collection = Item(MovieType, "Mouse Collection", 0);
        db.MediaItems.Add(collection);
        await db.SaveChangesAsync();
        var member = Item(MovieType, "The Mouse Trap", 1, collection.Id, year: 1932);
        db.MediaItems.Add(member);
        await db.SaveChangesAsync();

        (await MediaItemMatcher.FindByTitleYearAsync(db, "The Mouse Trap", 1932, MovieType, CancellationToken.None))!.Id.Should().Be(member.Id);
    }
}
