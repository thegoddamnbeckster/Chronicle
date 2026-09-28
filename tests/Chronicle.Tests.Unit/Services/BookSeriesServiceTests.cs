using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Per-user direction (2026-09-28): Hardcover's series data can't be fully trusted, so this must never
/// duplicate a book across a standalone/series change, a user's own manual placement always wins over a
/// later sync, and add/remove work exactly like a movie collection's own (not sticky on remove).
/// </summary>
public class BookSeriesServiceTests
{
    private static ChronicleDbContext NewDb() => new(new DbContextOptionsBuilder<ChronicleDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static BookSeriesService NewService() => new(null!, NullLogger<BookSeriesService>.Instance);

    private static async Task<(ChronicleDbContext db, MediaType type, MediaItem author)> SetupAsync()
    {
        var db = NewDb();
        var type = new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(type);
        var author = new MediaItem { MediaTypeId = 1, Name = "Author", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        return (db, type, author);
    }

    private static MediaItem Item(int typeId, string name, int level, int? parentId, int? number = null, string? json = null) => new()
    {
        MediaTypeId = typeId, Name = name, HierarchyLevel = level, ParentId = parentId, Number = number,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, MetadataJson = json,
    };

    private static MediaMetadata Book(string extId, string title, int? year = null, double? position = null) => new()
    {
        ExternalId = extId, Source = "hardcover", Title = title, Year = year,
        ExtendedData = position.HasValue ? JsonSerializer.SerializeToElement(new { seriesPosition = position }) : null,
    };

    private static Mock<IMetadataProvider> Provider(string name, params MediaMetadata[] results)
    {
        var mock = new Mock<IMetadataProvider>();
        mock.Setup(p => p.Name).Returns("hardcover");
        mock.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = name, Results = results.ToList() });
        return mock;
    }

    [Fact]
    public async Task MissingBooks_BecomeNumberedStubs()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "Some Series", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" });
        await db.SaveChangesAsync();

        var provider = Provider("Some Series", Book("hardcover:1", "Book One", 2001, 1), Book("hardcover:2", "Book Two", 2002, 2));
        var ok = await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        Assert.True(ok);
        var books = await db.MediaItems.Where(m => m.ParentId == series.Id).OrderBy(m => m.Number).ToListAsync();
        Assert.Equal(["Book One", "Book Two"], books.Select(b => b.Name));
        Assert.Equal([1, 2], books.Select(b => b.Number));
        Assert.All(books, b => Assert.True(b.IsStub));
    }

    [Fact]
    public async Task AnExistingStandaloneBook_IsReparentedIntoTheSeries_NotDuplicated()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "Some Series", 1, author.Id);
        var standalone = Item(1, "Book One", 1, author.Id); // currently a standalone book, level 1
        db.MediaItems.AddRange(series, standalone);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" },
            new MediaExternalId { MediaItemId = standalone.Id, Source = "hardcover", ExternalId = "hardcover:1" });
        await db.SaveChangesAsync();

        var provider = Provider("Some Series", Book("hardcover:1", "Book One", 2001, 1));
        await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        Assert.Equal(1, await db.MediaItems.CountAsync(m => m.MediaTypeId == 1 && m.Name == "Book One"));
        var moved = await db.MediaItems.FindAsync(standalone.Id);
        Assert.Equal((series.Id, 2, 1), (moved!.ParentId, moved.HierarchyLevel, moved.Number));
    }

    [Fact]
    public async Task ABookInAnotherSeriesUnderTheSameAuthor_IsMovedNotDuplicated()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var oldSeries = Item(1, "Old Series", 1, author.Id);
        var newSeries = Item(1, "New Series", 1, author.Id);
        db.MediaItems.AddRange(oldSeries, newSeries);
        await db.SaveChangesAsync();
        var book = Item(1, "Moved Book", 2, oldSeries.Id, number: 3);
        db.MediaItems.Add(book);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = newSeries.Id, Source = "hardcover", ExternalId = "hardcover:series:2" },
            new MediaExternalId { MediaItemId = book.Id, Source = "hardcover", ExternalId = "hardcover:9" });
        await db.SaveChangesAsync();

        var provider = Provider("New Series", Book("hardcover:9", "Moved Book", 2020, 1));
        await NewService().EnsureSeriesStubsAsync(db, newSeries, provider.Object, default);

        var moved = await db.MediaItems.FindAsync(book.Id);
        Assert.Equal((newSeries.Id, 1), (moved!.ParentId, moved.Number));
        Assert.Null(await db.MediaItems.FindAsync(oldSeries.Id)); // emptied series removed
    }

    [Fact]
    public async Task AManuallyPlacedBook_IsNeverTouchedByTheSync()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "Some Series", 1, author.Id);
        var manual = Item(1, "Manual Book", 1, author.Id); // user deliberately keeps this standalone
        db.MediaItems.AddRange(series, manual);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" },
            new MediaExternalId { MediaItemId = manual.Id, Source = "hardcover", ExternalId = "hardcover:1" },
            new MediaExternalId { MediaItemId = manual.Id, Source = "chronicle", ExternalId = "manual-series-member" });
        await db.SaveChangesAsync();

        var provider = Provider("Some Series", Book("hardcover:1", "Manual Book", 2001, 1));
        await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        var untouched = await db.MediaItems.FindAsync(manual.Id);
        Assert.Equal((author.Id, 1), (untouched!.ParentId, untouched.HierarchyLevel)); // left exactly as the user set it
    }

    [Fact]
    public async Task AWrongProviderMatch_RemovesTheExternalIdAndReportsFalse_WithoutTouchingChildren()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "My Series", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" });
        await db.SaveChangesAsync();

        var provider = Provider("A Totally Different Series", Book("hardcover:1", "Some Book"));
        var ok = await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        Assert.False(ok);
        Assert.Empty(await db.MediaExternalIds.Where(e => e.MediaItemId == series.Id).ToListAsync());
    }

    [Fact]
    public async Task AStubNoLongerInTheProvidersList_IsRemoved_ButAManuallyPlacedOneNeverIs()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "Some Series", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" });
        var staleStub = Item(1, "Gone Book", 2, series.Id, number: 5);
        staleStub.IsStub = true;
        db.MediaItems.Add(staleStub);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = staleStub.Id, Source = "hardcover", ExternalId = "hardcover:5" });
        await db.SaveChangesAsync();

        var provider = Provider("Some Series"); // now lists no books at all
        await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        Assert.Null(await db.MediaItems.FindAsync(staleStub.Id));
    }

    [Fact]
    public async Task ReparentIntoSeries_SetsTheManualMarker_AndRequiresTheSameAuthor()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var otherAuthor = new MediaItem { MediaTypeId = 1, Name = "Other Author", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(otherAuthor);
        await db.SaveChangesAsync();
        var series = Item(1, "Series", 1, author.Id);
        var book = Item(1, "Book", 1, author.Id);
        var wrongAuthorBook = Item(1, "Other Book", 1, otherAuthor.Id);
        db.MediaItems.AddRange(series, book, wrongAuthorBook);
        await db.SaveChangesAsync();

        var svc = NewService();
        await svc.ReparentIntoSeriesAsync(db, book.Id, series.Id);

        var moved = await db.MediaItems.FindAsync(book.Id);
        Assert.Equal((series.Id, 2), (moved!.ParentId, moved.HierarchyLevel));
        Assert.Contains(await db.MediaExternalIds.Where(e => e.MediaItemId == book.Id).ToListAsync(),
            e => e.ExternalId == "manual-series-member");

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReparentIntoSeriesAsync(db, wrongAuthorBook.Id, series.Id));
    }

    [Fact]
    public async Task RemoveFromSeries_IsNotSticky_ClearsTheMarkerAndRemovesAnEmptiedSeries()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "Series", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        var book = Item(1, "Book", 2, series.Id, number: 1);
        db.MediaItems.Add(book);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = book.Id, Source = "chronicle", ExternalId = "manual-series-member" });
        await db.SaveChangesAsync();

        await NewService().RemoveFromSeriesAsync(db, book.Id);

        var moved = await db.MediaItems.FindAsync(book.Id);
        Assert.Equal((author.Id, 1, (int?)null), (moved!.ParentId, moved.HierarchyLevel, moved.Number));
        Assert.Empty(await db.MediaExternalIds.Where(e => e.MediaItemId == book.Id).ToListAsync()); // marker cleared, not sticky
        Assert.Null(await db.MediaItems.FindAsync(series.Id)); // no children left -- removed
    }
}
