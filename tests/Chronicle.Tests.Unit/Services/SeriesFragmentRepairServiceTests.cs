using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>Live (2026-09-27): a series tag "Laundry Files #5" made one series item per book.</summary>
public class SeriesFragmentRepairServiceTests
{
    private static (ChronicleDbContext db, SeriesFragmentRepairService svc) Setup()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        return (db, new SeriesFragmentRepairService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SeriesFragmentRepairService>.Instance));
    }

    private static MediaItem Item(string name, int level, int? parentId, int? number = null) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = level, ParentId = parentId, Number = number,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task FragmentsFoldIntoTheExistingSeries_BooksKeepOrGainTheirPosition_AndFragmentsAreRemoved()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Charles Stross", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var whole = Item("The Laundry Files", 1, author.Id);
        var frag5 = Item("Laundry Files #5", 1, author.Id);
        var frag7 = Item("The Laundry Files #7", 1, author.Id);
        db.MediaItems.AddRange(whole, frag5, frag7);
        await db.SaveChangesAsync();
        var one = Item("The Atrocity Archives", 2, whole.Id, number: 1);
        var five = Item("The Rhesus Chart", 2, frag5.Id);
        var seven = Item("The Nightmare Stacks", 2, frag7.Id);
        db.MediaItems.AddRange(one, five, seven);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Equal(whole.Id, (await db.MediaItems.FindAsync(five.Id))!.ParentId);
        Assert.Equal(5, (await db.MediaItems.FindAsync(five.Id))!.Number);
        Assert.Equal(whole.Id, (await db.MediaItems.FindAsync(seven.Id))!.ParentId);
        Assert.Equal(7, (await db.MediaItems.FindAsync(seven.Id))!.Number);
        Assert.Equal(1, (await db.MediaItems.FindAsync(one.Id))!.Number);
        Assert.Null(await db.MediaItems.FindAsync(frag5.Id));
        Assert.Null(await db.MediaItems.FindAsync(frag7.Id));
        Assert.Equal("The Laundry Files", (await db.MediaItems.FindAsync(whole.Id))!.Name);
    }

    [Fact]
    public async Task WithNoBareSeries_TheFirstFragmentIsRenamedAndTheOthersJoinIt()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var a = Item("Wandering #1", 1, author.Id);
        var b = Item("Wandering #7", 1, author.Id);
        db.MediaItems.AddRange(a, b);
        await db.SaveChangesAsync();
        var book1 = Item("One", 2, a.Id);
        var book7 = Item("Seven", 2, b.Id);
        db.MediaItems.AddRange(book1, book7);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Equal("Wandering", (await db.MediaItems.FindAsync(a.Id))!.Name);
        Assert.Equal(a.Id, (await db.MediaItems.FindAsync(book7.Id))!.ParentId);
        Assert.Equal((1, 7), ((await db.MediaItems.FindAsync(book1.Id))!.Number, (await db.MediaItems.FindAsync(book7.Id))!.Number));
        Assert.Null(await db.MediaItems.FindAsync(b.Id));
    }

    [Fact]
    public async Task ASeriesIsNeverFoldedIntoAnotherAuthorsSeries()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var a1 = Item("A1", 0, null);
        var a2 = Item("A2", 0, null);
        db.MediaItems.AddRange(a1, a2);
        await db.SaveChangesAsync();
        var s1 = Item("Saga", 1, a1.Id);
        var s2 = Item("Saga #2", 1, a2.Id);
        db.MediaItems.AddRange(s1, s2);
        await db.SaveChangesAsync();
        var b1 = Item("B1", 2, s1.Id);
        var b2 = Item("B2", 2, s2.Id);
        db.MediaItems.AddRange(b1, b2);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Equal(s1.Id, (await db.MediaItems.FindAsync(b1.Id))!.ParentId);
        Assert.Equal(s2.Id, (await db.MediaItems.FindAsync(b2.Id))!.ParentId);
        Assert.Equal("Saga", (await db.MediaItems.FindAsync(s2.Id))!.Name);   // its own author's lone fragment is only renamed
    }

    [Theory]
    [InlineData("Laundry Files #5", "Laundry Files", 5)]
    [InlineData("Backyard Starship, Book #29", "Backyard Starship", 29)]
    [InlineData("The Wandering Inn #1", "The Wandering Inn", 1)]
    [InlineData("Series (#3)", "Series", 3)]
    [InlineData("Dungeon Crawler Carl", "Dungeon Crawler Carl", null)]
    [InlineData("Catch-22", "Catch-22", null)]
    public void SplitSeriesTag_SeparatesTheNameFromThePosition(string tag, string name, int? number)
    {
        Assert.Equal((name, number), FileScanService.SplitSeriesTag(tag));
    }
}
