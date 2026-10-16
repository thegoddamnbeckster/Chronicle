using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A book put into a series by hand has no provider list to take its position from (and the provider may be
/// out of quota), so it sorted after every positioned book ("Mark of the Fool 5" and "10", 2026-10-15). The
/// position is now read from a title of the form "&lt;series&gt; N", when the book is added and by the repair pass.
/// </summary>
public class SeriesPositionFromTitleTests
{
    [Theory]
    [InlineData("Mark of the Fool 5", "Mark of the Fool", 5.0)]
    [InlineData("  mark of the fool 10 ", "Mark of the Fool", 10.0)]
    [InlineData("Mark of the Fool 2.5", "Mark of the Fool", 2.5)]
    [InlineData("Dr. Who (2) 4", "Dr. Who (2)", 4.0)]
    public void InferFromTitle_ReadsTheNumberAfterTheSeriesName(string title, string series, double expected) =>
        Assert.Equal(expected, SeriesPositionHelper.InferFromTitle(title, series));

    [Theory]
    [InlineData("Mark of the Fool", "Mark of the Fool")]               // the series name alone
    [InlineData("Fahrenheit 451", "Mark of the Fool")]                  // an unrelated title ending in digits
    [InlineData("Mark of the Fool: Origins 3", "Mark of the Fool")]     // a subtitle
    [InlineData("DrX Who (2) 4", "Dr. Who (2)")]                        // regex characters are literal
    [InlineData("Mark of the Fool 5", null)]
    [InlineData(null, "Mark of the Fool")]
    public void InferFromTitle_ReturnsNullWhenTheTitleIsNotSeriesPlusNumber(string? title, string? series) =>
        Assert.Null(SeriesPositionHelper.InferFromTitle(title, series));

    private static ChronicleDbContext NewDb()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, SupportsCollections = true, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return db;
    }

    private static MediaItem Item(string name, int level, int? parentId, int? number = null) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = level, ParentId = parentId, Number = number,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task AddingABookToASeriesByHand_GivesItThePositionFromItsTitle()
    {
        await using var db = NewDb();
        var author = Item("J.M. Clarke", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("Mark of the Fool", 1, author.Id);
        var loose5 = Item("Mark of the Fool 5", 1, author.Id);
        var loose10 = Item("Mark of the Fool 10", 1, author.Id);
        var oddTitle = Item("A Standalone Tale", 1, author.Id);
        db.MediaItems.AddRange(series, loose5, loose10, oddTitle);
        await db.SaveChangesAsync();
        db.MediaItems.Add(Item("Mark of the Fool 4", 2, series.Id, number: 4));
        await db.SaveChangesAsync();

        var svc = new BookSeriesService(NullLogger<BookSeriesService>.Instance);
        await svc.ReparentIntoSeriesAsync(db, loose5.Id, series.Id);
        await svc.ReparentIntoSeriesAsync(db, loose10.Id, series.Id);
        await svc.ReparentIntoSeriesAsync(db, oddTitle.Id, series.Id);

        var five = (await db.MediaItems.FindAsync(loose5.Id))!;
        Assert.Equal((5, 5.0), (five.Number, five.SeriesPosition));
        var ten = (await db.MediaItems.FindAsync(loose10.Id))!;
        Assert.Equal((10, 10.0), (ten.Number, ten.SeriesPosition));
        var odd = (await db.MediaItems.FindAsync(oddTitle.Id))!;
        Assert.Null(odd.Number);   // no position can be read from the title, so none is invented
    }

    [Fact]
    public async Task AddingABookThatAlreadyHasAPosition_KeepsIt()
    {
        await using var db = NewDb();
        var author = Item("J.M. Clarke", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("Mark of the Fool", 1, author.Id);
        var book = Item("Mark of the Fool 5", 1, author.Id, number: 7);
        db.MediaItems.AddRange(series, book);
        await db.SaveChangesAsync();
        db.MediaItems.Add(Item("Mark of the Fool 1", 2, series.Id, number: 1));
        await db.SaveChangesAsync();

        await new BookSeriesService(NullLogger<BookSeriesService>.Instance).ReparentIntoSeriesAsync(db, book.Id, series.Id);

        Assert.Equal(7, (await db.MediaItems.FindAsync(book.Id))!.Number);
    }

    [Fact]
    public async Task TheRepairPass_FillsAnEmptyPositionFromTheTitle_AndLeavesTheRestAlone()
    {
        await using var db = NewDb();
        var author = Item("J.M. Clarke", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("Mark of the Fool", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        var five = Item("Mark of the Fool 5", 2, series.Id);
        var fixedByHand = Item("Mark of the Fool 6", 2, series.Id, number: 9);
        var unrelated = Item("Interlude", 2, series.Id);
        db.MediaItems.AddRange(five, fixedByHand, unrelated);
        await db.SaveChangesAsync();
        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var repair = new AudiobookSeriesNumberRepairService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AudiobookSeriesNumberRepairService>.Instance);

        await repair.ExecuteAsync(default);

        Assert.Equal((5, 5.0), ((await db.MediaItems.FindAsync(five.Id))!.Number, (await db.MediaItems.FindAsync(five.Id))!.SeriesPosition));
        Assert.Equal(9, (await db.MediaItems.FindAsync(fixedByHand.Id))!.Number);   // never overwritten
        Assert.Null((await db.MediaItems.FindAsync(unrelated.Id))!.Number);
    }
}
