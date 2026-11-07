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

    // ── music: a band is credited on every track; the person page shows the albums ─────────────────────────────────
    // Live (2026-11-07): 3 Doors Down's page listed 185 individual tracks, each with its album's artwork, under "Actor".

    private static async Task<(ChronicleDbContext Db, MediaItem Artist, MediaItem AlbumA, MediaItem AlbumB, MediaItem Track1, MediaItem Track2, MediaItem Track3)> SeedMusicAsync()
    {
        var db = NewDb();
        var music = new MediaType { Id = 3, Name = "music", DisplayName = "Music", HierarchyLevels = 3, ProviderFamily = ProviderFamilies.Music, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(music);
        MediaItem Item(string name, int level, int? parent, int? year) => new()
        {
            Name = name, MediaTypeId = music.Id, MediaType = music, HierarchyLevel = level, ParentId = parent, Year = year,
            PosterUrl = $"https://img/{name}.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var artist = Item("3 Doors Down", 0, null, null);
        db.MediaItems.Add(artist);
        await db.SaveChangesAsync();
        var albumA = Item("Us and the Night", 1, artist.Id, 2016);
        var albumB = Item("Seventeen Days", 1, artist.Id, 2005);
        db.MediaItems.AddRange(albumA, albumB);
        await db.SaveChangesAsync();
        var t1 = Item("Inside of Me", 2, albumA.Id, 2016);
        var t2 = Item("Pieces of Me", 2, albumA.Id, 2016);
        var t3 = Item("Let Me Go", 2, albumB.Id, 2005);
        db.MediaItems.AddRange(t1, t2, t3);
        await db.SaveChangesAsync();
        return (db, artist, albumA, albumB, t1, t2, t3);
    }

    [Fact]
    public async Task Tracks_BecomeTheirAlbum_AndTwoSourcesNamingTheSameArtistAreOneCard()
    {
        RegistryFixtures.LoadBuiltIns();
        var (db, _, albumA, albumB, t1, t2, t3) = await SeedMusicAsync();
        await using var _ = db;
        // MusicBrainz says "Cast", Deezer says "Main", on every track; MusicBrainz also credits one album itself
        var credits = new[]
        {
            Credit(t1, "Cast", null), Credit(t1, "Main", null), Credit(t2, "Cast", null), Credit(t2, "Main", null),
            Credit(t3, "Cast", null), Credit(albumA, "Cast", null),
        };

        var rows = await PersonCreditRollup.RollUpAsync(db, credits);

        Assert.Equal(2, rows.Count);   // two albums, once each: not six cards
        Assert.All(rows, r => Assert.Equal("Artist", r.Role));
        Assert.Contains(rows, r => r.MediaItemId == albumA.Id && r.Name == "Us and the Night" && r.Year == 2016);
        Assert.Contains(rows, r => r.MediaItemId == albumB.Id && r.Name == "Seventeen Days");
        Assert.DoesNotContain(rows, r => r.MediaItemId == t1.Id || r.MediaItemId == t2.Id || r.MediaItemId == t3.Id);
    }

    [Fact]
    public async Task AnAlbumCredit_StaysTheAlbum_AndTheArtistItemIsNotWhatTracksClimbTo()
    {
        RegistryFixtures.LoadBuiltIns();
        var (db, artist, albumA, _, t1, _, _) = await SeedMusicAsync();
        await using var _ = db;

        var rows = await PersonCreditRollup.RollUpAsync(db, [Credit(t1, "Cast", null), Credit(albumA, "Main", null)]);

        Assert.Equal(albumA.Id, Assert.Single(rows).MediaItemId);
        Assert.DoesNotContain(rows, r => r.MediaItemId == artist.Id);
    }

    [Fact]
    public async Task AMusicalRoleOtherThanCastIsKept()
    {
        RegistryFixtures.LoadBuiltIns();
        var (db, _, albumA, _, t1, _, _) = await SeedMusicAsync();
        await using var _ = db;

        var rows = await PersonCreditRollup.RollUpAsync(db, [Credit(t1, "Composer", null), Credit(albumA, "Producer", null)]);

        Assert.Contains(rows, r => r.Role == "Composer" && r.MediaItemId == albumA.Id);
        Assert.Contains(rows, r => r.Role == "Producer" && r.MediaItemId == albumA.Id);
    }

    // ── other kinds of media: the item is the work, and cast are not called actors ───────────────────────────────

    private static async Task<(ChronicleDbContext Db, MediaItem Item)> SeedOneAsync(string typeName, int levels = 1, int level = 0)
    {
        var db = NewDb();
        var t = new MediaType { Id = 9, Name = typeName, DisplayName = typeName, HierarchyLevels = levels, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(t);
        var item = new MediaItem { Name = "Work", MediaTypeId = t.Id, MediaType = t, HierarchyLevel = level, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        return (db, item);
    }

    [Theory]
    [InlineData("game", 1, 0)]         // a studio is credited as cast on a game
    [InlineData("audiobooks", 3, 2)]   // a narrator is credited as cast on an audiobook
    public async Task CastOnAKindOfMediaWhereCastDoNotAct_KeepsItsRole_NotActor(string typeName, int levels, int level)
    {
        RegistryFixtures.LoadBuiltIns();
        var (db, item) = await SeedOneAsync(typeName, levels, level);
        await using var _ = db;

        var rows = await PersonCreditRollup.RollUpAsync(db, [Credit(item, "Cast", null)]);

        var only = Assert.Single(rows);
        Assert.Equal("Cast", only.Role);
        Assert.Equal(item.Id, only.MediaItemId);   // and the item itself is the card
    }

    [Fact]
    public async Task ABook_IsItsOwnWork_NotRolledUpToItsSeriesOrAuthor()
    {
        RegistryFixtures.LoadBuiltIns();
        var (db, book) = await SeedOneAsync("books", 3, 2);
        await using var _ = db;
        book.ParentId = book.Id + 100;   // a parent exists, but a book is the work

        var rows = await PersonCreditRollup.RollUpAsync(db, [Credit(book, "Author", null)]);

        var only = Assert.Single(rows);
        Assert.Equal(book.Id, only.MediaItemId);
        Assert.Equal("Author", only.Role);
    }

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
