using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class FolderNameYearTests
{
    [Theory]
    [InlineData("(2000) The Better Life", "The Better Life", 2000)]
    [InlineData("(2000)The Better Life", "The Better Life", 2000)]
    [InlineData("(2000) - The Better Life", "The Better Life", 2000)]
    [InlineData("The Better Life (2000)", "The Better Life", 2000)]
    [InlineData("The Better Life [2000]", "The Better Life", 2000)]
    [InlineData("3121 (2006)", "3121", 2006)]
    [InlineData("Plain", "Plain", null)]
    [InlineData("Live (Remastered)", "Live (Remastered)", null)]
    [InlineData("(2026 Repented)", "(2026 Repented)", null)]
    [InlineData("(2000)", "(2000)", null)]
    [InlineData("(9999) Far Future", "(9999) Far Future", null)]
    [InlineData("1999", "1999", null)]
    public void Split(string input, string name, int? year) =>
        FolderNameYear.Split(input).Should().Be((name, year));
}

public class AlbumNameYearRepairServiceTests
{
    private static (ChronicleDbContext db, AlbumNameYearRepairService svc) Make()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 3, Name = "music", DisplayName = "Music", HierarchyLevels = 3, HierarchyLabels = "Artist,Album,Track", CreatedAt = DateTime.UtcNow });
        db.MediaTypes.Add(new MediaType { Id = 2, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, HierarchyLabels = "Show,Season,Episode", CreatedAt = DateTime.UtcNow });
        var sp = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        return (db, new AlbumNameYearRepairService(sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AlbumNameYearRepairService>.Instance));
    }

    private static MediaItem Item(int type, string name, int level, int? year = null, int? parent = null) => new()
    {
        MediaTypeId = type, Name = name, HierarchyLevel = level, Year = year, ParentId = parent,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task AlbumNames_LoseTheirYear_AndTheYearMovesToTheYearField()
    {
        var (db, svc) = Make();
        db.MediaItems.AddRange(Item(3, "(2000) The Better Life", 1), Item(3, "Stay (2013)", 1),
            Item(3, "Plain", 1), Item(3, "Live (Remastered)", 1));
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        var byName = (await db.MediaItems.ToListAsync()).ToDictionary(i => i.Name, i => i.Year);
        byName.Should().BeEquivalentTo(new Dictionary<string, int?>
        {
            ["The Better Life"] = 2000, ["Stay"] = 2013, ["Plain"] = null, ["Live (Remastered)"] = null,
        });
    }

    [Fact]
    public async Task AnAlbumThatAlreadyHasAYear_KeepsIt()
    {
        var (db, svc) = Make();
        db.MediaItems.Add(Item(3, "(2000) The Better Life", 1, year: 2001));
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        var album = await db.MediaItems.SingleAsync();
        (album.Name, album.Year).Should().Be(("The Better Life", 2001));
    }

    [Fact]
    public async Task OnlyTheAlbumLevelOfAnAlbumType_IsTouched()
    {
        var (db, svc) = Make();
        db.MediaItems.AddRange(
            Item(3, "Band (1990)", 0), Item(3, "Song (2000)", 2),     // artist and track levels
            Item(2, "Show (2008)", 1));                               // a type whose level 1 is a Season
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        (await db.MediaItems.Select(i => i.Name).ToListAsync())
            .Should().BeEquivalentTo("Band (1990)", "Song (2000)", "Show (2008)");
    }
}
