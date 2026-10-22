using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>Live (2026-10-22): Jason Isaacs's page listed 22 individual Star Trek: Discovery episodes instead of the show.</summary>
public class PersonCreditRollupTests
{
    private static ChronicleDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(ChronicleDbContext Db, MediaItem Show, MediaItem Season, MediaItem Ep1, MediaItem Ep2, MediaItem Movie)> SeedAsync()
    {
        var db = NewDb();
        var tv = new MediaType { Id = 1, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow };
        var movies = new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.AddRange(tv, movies);
        MediaItem Item(string name, MediaType t, int level, int? parent, int? year) => new()
        {
            Name = name, MediaTypeId = t.Id, MediaType = t, HierarchyLevel = level, ParentId = parent, Year = year,
            PosterUrl = $"https://img/{name}.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var show = Item("Star Trek: Discovery", tv, 0, null, 2017);
        db.MediaItems.Add(show);
        await db.SaveChangesAsync();
        var season = Item("Season 1", tv, 1, show.Id, 2017);
        db.MediaItems.Add(season);
        await db.SaveChangesAsync();
        var ep1 = Item("Despite Yourself", tv, 2, season.Id, 2018);
        var ep2 = Item("Nothing Is Simple Anymore", tv, 2, season.Id, 2019);
        var movie = Item("Look Away", movies, 0, null, 2018);
        db.MediaItems.AddRange(ep1, ep2, movie);
        await db.SaveChangesAsync();
        return (db, show, season, ep1, ep2, movie);
    }

    private static MediaCredit Credit(MediaItem item, string role, string? character) =>
        new() { MediaItemId = item.Id, MediaItem = item, Role = role, CharacterName = character, PersonName = "Jason Isaacs", Source = "imdb" };

    [Fact]
    public async Task EpisodesOfOneShow_BecomeOneCard_WithTheCharactersMerged()
    {
        RegistryFixtures.LoadBuiltIns();
        var (db, show, _, ep1, ep2, _) = await SeedAsync();
        await using var _ = db;
        var credits = new[] { Credit(ep1, "Actor", "Captain Gabriel Lorca"), Credit(ep2, "Actor", "The Emperor"), Credit(show, "Actor", "Captain Gabriel Lorca") };

        var rows = await PersonCreditRollup.RollUpAsync(db, credits);

        var only = Assert.Single(rows);
        Assert.Equal(show.Id, only.MediaItemId);
        Assert.Equal("Star Trek: Discovery", only.Name);
        Assert.Equal(2017, only.Year);
        Assert.Equal("Captain Gabriel Lorca / The Emperor", only.CharacterName);
    }

    [Fact]
    public async Task AMovie_IsLeftAlone_AndRolesStaySeparate()
    {
        RegistryFixtures.LoadBuiltIns();
        var (db, show, _, ep1, _, movie) = await SeedAsync();
        await using var _ = db;
        var credits = new[] { Credit(ep1, "Actor", "Lorca"), Credit(movie, "Actor", "Dan"), Credit(ep1, "Director", null) };

        var rows = await PersonCreditRollup.RollUpAsync(db, credits);

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.MediaItemId == movie.Id && r.Role == "Actor" && r.CharacterName == "Dan");
        Assert.Contains(rows, r => r.MediaItemId == show.Id && r.Role == "Actor");
        Assert.Contains(rows, r => r.MediaItemId == show.Id && r.Role == "Director" && r.CharacterName is null);
    }

    [Fact]
    public async Task ASeasonCredit_RollsUpToTheShowToo()
    {
        RegistryFixtures.LoadBuiltIns();
        var (db, show, season, _, _, _) = await SeedAsync();
        await using var _ = db;

        var rows = await PersonCreditRollup.RollUpAsync(db, [Credit(season, "Actor", "Lorca")]);

        Assert.Equal(show.Id, Assert.Single(rows).MediaItemId);
    }

    [Theory]
    [InlineData("Ra's al Ghul (voice)|Ra's al Ghul", "Ra's al Ghul (voice)")]
    [InlineData("Captain Gabriel Lorca|USS Discovery Captain Gabriel Lorca", "USS Discovery Captain Gabriel Lorca")]
    [InlineData("Lord Felton|Felton", "Lord Felton")]
    [InlineData("Captain Gabriel Lorca|The Emperor|Captain Gabriel Lorca", "Captain Gabriel Lorca / The Emperor")]
    [InlineData("James Hook / George Darling|Mr. Darling|Captain Hook", "James Hook / George Darling / Mr. Darling / Captain Hook")]
    [InlineData("Volmer|Volmer", "Volmer")]
    [InlineData("Dan|Jordan", "Dan / Jordan")]
    [InlineData("Ann|Hannah", "Ann / Hannah")]
    [InlineData("Al|Alfred Pennyworth", "Al / Alfred Pennyworth")]
    public void MergeCharacters_KeepsOnePartPerCharacter_PreferringTheFullerSpelling(string joined, string expected) =>
        Assert.Equal(expected, PersonCreditRollup.MergeCharacters(joined.Split('|')));

    [Fact]
    public void MergeCharacters_IsNullWhenNoOneIsNamed() =>
        Assert.Null(PersonCreditRollup.MergeCharacters([null, " ", ""]));

    [Fact]
    public async Task CastAndActor_AreOneRole()
    {
        RegistryFixtures.LoadBuiltIns();
        var (db, show, _, _, _, movie) = await SeedAsync();
        await using var _ = db;

        var rows = await PersonCreditRollup.RollUpAsync(db, [Credit(movie, "Cast", "Dan"), Credit(movie, "Actor", "Dan")]);

        var only = Assert.Single(rows);
        Assert.Equal("Actor", only.Role);
    }
}
