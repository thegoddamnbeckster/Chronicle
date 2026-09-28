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
        mock.Setup(p => p.GetSupportedMediaTypes()).Returns([new MediaTypeSupport { MediaTypeName = "audiobooks" }]);
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
        var existingSeriesBook = Item(1, "Already In It", 2, series.Id, number: 1); // a series needs a book of its own to be a real target
        db.MediaItems.Add(existingSeriesBook);
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

    [Fact]
    public async Task ReparentIntoSeries_RejectsATargetWithNoBooksOfItsOwn()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var emptySeries = Item(1, "Not Really A Series", 1, author.Id); // structurally identical to a standalone book
        var book = Item(1, "Book", 1, author.Id);
        db.MediaItems.AddRange(emptySeries, book);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().ReparentIntoSeriesAsync(db, book.Id, emptySeries.Id));
        Assert.Equal((author.Id, 1), ((await db.MediaItems.FindAsync(book.Id))!.ParentId, (await db.MediaItems.FindAsync(book.Id))!.HierarchyLevel));
    }

    [Fact]
    public async Task ExternalIdMatch_IsCaseInsensitive()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "Some Series", 1, author.Id);
        var standalone = Item(1, "Book One", 1, author.Id);
        db.MediaItems.AddRange(series, standalone);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" },
            // Stored in a different case than what the provider returns below.
            new MediaExternalId { MediaItemId = standalone.Id, Source = "hardcover", ExternalId = "HARDCOVER:1" });
        await db.SaveChangesAsync();

        var provider = Provider("Some Series", Book("hardcover:1", "Book One", 2001, 1));
        await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        Assert.Equal(1, await db.MediaItems.CountAsync(m => m.Name == "Book One")); // matched, not duplicated
        var moved = await db.MediaItems.FindAsync(standalone.Id);
        Assert.Equal(series.Id, moved!.ParentId);
    }

    [Fact]
    public async Task MovingToADifferentSeriesWithNoKnownPosition_ClearsTheStaleNumber_RatherThanKeepingTheOldSeriesPosition()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var oldSeries = Item(1, "Old Series", 1, author.Id);
        var newSeries = Item(1, "New Series", 1, author.Id);
        db.MediaItems.AddRange(oldSeries, newSeries);
        await db.SaveChangesAsync();
        var book = Item(1, "Moved Book", 2, oldSeries.Id, number: 5); // #5 in the OLD series
        var anchor = Item(1, "Anchor", 2, newSeries.Id, number: 1);   // gives newSeries a book of its own
        db.MediaItems.AddRange(book, anchor);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = newSeries.Id, Source = "hardcover", ExternalId = "hardcover:series:2" },
            new MediaExternalId { MediaItemId = book.Id, Source = "hardcover", ExternalId = "hardcover:9" });
        await db.SaveChangesAsync();

        // Hardcover now lists the book in New Series but with no position for it there.
        var provider = Provider("New Series", Book("hardcover:9", "Moved Book"));
        await NewService().EnsureSeriesStubsAsync(db, newSeries, provider.Object, default);

        var moved = await db.MediaItems.FindAsync(book.Id);
        Assert.Equal((newSeries.Id, (int?)null), (moved!.ParentId, moved.Number)); // moved, but #5 did not follow it here
        Assert.Null(moved.SeriesPosition); // and the old series' precise position didn't survive either
    }

    [Fact]
    public async Task FractionalPositions_ArePreservedOnSeriesPosition_SoTheyDontCollideOnNumber()
    {
        // Root-caused live (2026-09-29): Hardcover's "The Expanse" lists a companion novella at
        // position 1.1 between the first two novels -- flooring to Number alone made two
        // different books both show "#1" in the series list. SeriesPosition carries the precise
        // value through so the UI can tell them apart, while Number keeps the floor for every
        // other generic ordinal use (sorting fallback, next/prev nav).
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "The Expanse", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" });
        await db.SaveChangesAsync();

        var provider = Provider("The Expanse",
            Book("hardcover:1", "Leviathan Wakes", 2011, 1),
            Book("hardcover:2", "The Butcher of Anderson Station", 2011, 1.1),
            Book("hardcover:3", "Caliban's War", 2012, 2));
        await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        var books = await db.MediaItems.Where(m => m.ParentId == series.Id).OrderBy(m => m.SeriesPosition).ToListAsync();
        Assert.Equal(["Leviathan Wakes", "The Butcher of Anderson Station", "Caliban's War"], books.Select(b => b.Name));
        Assert.Equal([1, 1, 2], books.Select(b => b.Number)); // still both floor to 1 -- Number alone can't disambiguate
        Assert.Equal([1.0, 1.1, 2.0], books.Select(b => b.SeriesPosition)); // SeriesPosition can
    }

    [Fact]
    public async Task ABookKeepsItsKnownPosition_WhenTheProviderOmitsItOnALaterSync_RatherThanGoingStale()
    {
        // Code review (2026-09-29): the `changed` predicate's SeriesPosition comparison was
        // unguarded, so a book keeping its old fractional SeriesPosition while a later sync omits
        // seriesPosition entirely (still the same series) made `changed` true purely from
        // "1.1 != null" -- entering the update block, bumping UpdatedAt and logging, even though
        // neither branch inside it actually touches Number/SeriesPosition for this combination
        // (floorPosition has no value, and the book isn't moving series). That's a wasted write+log
        // on every future pass with no corresponding data change -- gating the comparison the same
        // way as the Number clause (position.HasValue &&) fixes it: `changed` should stay false, and
        // the book's existing values must be left exactly as they were.
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "Some Series", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        var book = Item(1, "Book One", 2, series.Id, number: 1);
        book.SeriesPosition = 1.1;
        db.MediaItems.Add(book);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" },
            new MediaExternalId { MediaItemId = book.Id, Source = "hardcover", ExternalId = "hardcover:1" });
        await db.SaveChangesAsync();
        var originalUpdatedAt = book.UpdatedAt;

        // Later sync: same book, same series, but this time Hardcover's response carries no
        // seriesPosition at all.
        var provider = Provider("Some Series", Book("hardcover:1", "Book One", 2001, position: null));
        await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        var unchanged = await db.MediaItems.FindAsync(book.Id);
        Assert.Equal((series.Id, 1, 1.1, originalUpdatedAt), (unchanged!.ParentId, unchanged.Number, unchanged.SeriesPosition, unchanged.UpdatedAt));
    }

    [Fact]
    public async Task ARealItemMatchedToAnAlternateEditionId_IsNotDuplicated_AndAnExistingStubForTheSlotIsRemoved()
    {
        // Root-caused live (2026-09-28): Dungeon Crawler Carl #1 was a real library item carrying
        // hardcover:2333832 while the collapsed one-per-position representative was
        // hardcover:446681 -- neither the id match nor the title+year fallback ("...: A LitRPG
        // Adventure", 2021 vs 2020) found it, so a stub was minted beside every real item.
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "Dungeon Crawler Carl", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        var real = Item(1, "Dungeon Crawler Carl: A LitRPG Adventure", 2, series.Id, number: 1);
        var oldStub = Item(1, "Dungeon Crawler Carl", 2, series.Id, number: 1);
        oldStub.IsStub = true;
        db.MediaItems.AddRange(real, oldStub);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" },
            new MediaExternalId { MediaItemId = real.Id, Source = "hardcover", ExternalId = "hardcover:2333832" },
            new MediaExternalId { MediaItemId = oldStub.Id, Source = "hardcover", ExternalId = "hardcover:446681" });
        await db.SaveChangesAsync();

        var book = Book("hardcover:446681", "Dungeon Crawler Carl", 2020, 1);
        book.ExtendedData = JsonSerializer.SerializeToElement(new { seriesPosition = 1.0, alternateIds = new[] { "hardcover:2333832" } });
        var provider = Provider("Dungeon Crawler Carl", book);
        await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        var books = await db.MediaItems.Where(m => m.ParentId == series.Id).ToListAsync();
        Assert.Equal([real.Id], books.Select(b => b.Id));
    }

    [Fact]
    public async Task ARateLimitedProvider_PropagatesTheError_AndKeepsTheSeriesExternalId()
    {
        // A throttled Hardcover says nothing about whether the stored series id is bad -- the old
        // catch-everything branch deleted the id on ANY fetch failure, which during a rate-limit
        // storm would have stripped it from every series in the sweep.
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "Some Series", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" });
        await db.SaveChangesAsync();
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.Name).Returns("hardcover");
        provider.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("quota", null, System.Net.HttpStatusCode.TooManyRequests));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default));

        Assert.True(await db.MediaExternalIds.AnyAsync(e => e.MediaItemId == series.Id && e.ExternalId == "hardcover:series:1"));
    }

    private static async Task<MediaItem> SeriesWithHcIdAsync(ChronicleDbContext db, MediaItem author)
    {
        var series = Item(1, "Some Series", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" });
        await db.SaveChangesAsync();
        return series;
    }

    private static MediaMetadata BookWithAlternates(string id, string title, double position, params string[] alternates)
    {
        var b = Book(id, title, 2001, position);
        b.ExtendedData = JsonSerializer.SerializeToElement(new { seriesPosition = position, alternateIds = alternates });
        return b;
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))] // what HardcoverClient throws for a persistent 401/403 or a GraphQL error
    [InlineData(typeof(TimeoutException))]
    public async Task ATransientOrAuthFailure_NeverDeletesTheSeriesExternalId(Type exceptionType)
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = await SeriesWithHcIdAsync(db, author);
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.Name).Returns("hardcover");
        provider.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType, "boom")!);

        await Assert.ThrowsAsync(exceptionType, () => NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default));

        Assert.True(await db.MediaExternalIds.AnyAsync(e => e.MediaItemId == series.Id && e.ExternalId == "hardcover:series:1"));
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(KeyNotFoundException))]
    public async Task AnIdTheProviderSaysIsMalformedOrUnknown_IsRemoved(Type exceptionType)
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = await SeriesWithHcIdAsync(db, author);
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.Name).Returns("hardcover");
        provider.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType, "gone")!);

        var ok = await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        Assert.True(ok);
        Assert.False(await db.MediaExternalIds.AnyAsync(e => e.MediaItemId == series.Id && e.ExternalId == "hardcover:series:1"));
    }

    [Fact]
    public async Task SeveralStubsForOneSlot_AreCollapsedToTheRepresentativeOne()
    {
        // The Ready Player One shape: per-edition stubs at one position from before the collapse fix.
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = await SeriesWithHcIdAsync(db, author);
        foreach (var id in new[] { "hardcover:20", "hardcover:10", "hardcover:30" })
        {
            var stub = Item(1, "Ready Player One", 2, series.Id, number: 1);
            stub.IsStub = true;
            db.MediaItems.Add(stub);
            await db.SaveChangesAsync();
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = stub.Id, Source = "hardcover", ExternalId = id });
        }
        await db.SaveChangesAsync();

        var provider = Provider("Some Series", BookWithAlternates("hardcover:10", "Ready Player One", 1, "hardcover:20", "hardcover:30"));
        await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        var left = await db.MediaItems.Where(m => m.ParentId == series.Id).Include(m => m.ExternalIds).ToListAsync();
        Assert.Equal(["hardcover:10"], left.SelectMany(m => m.ExternalIds).Select(e => e.ExternalId));
    }

    [Fact]
    public async Task AStubTheUserHasAddedToTheirLibrary_IsNeverAutoRemoved()
    {
        // Removing a media item cascade-deletes the user's library entry / play history / list entries.
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = await SeriesWithHcIdAsync(db, author);
        var real = Item(1, "Dungeon Crawler Carl: A LitRPG Adventure", 2, series.Id, number: 1);
        var stub = Item(1, "Dungeon Crawler Carl", 2, series.Id, number: 1);
        stub.IsStub = true;
        db.MediaItems.AddRange(real, stub);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = real.Id, Source = "hardcover", ExternalId = "hardcover:2" },
            new MediaExternalId { MediaItemId = stub.Id, Source = "hardcover", ExternalId = "hardcover:1" });
        db.UserLibraries.Add(new UserLibrary { UserId = 1, MediaItemId = stub.Id, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var provider = Provider("Some Series", BookWithAlternates("hardcover:1", "Dungeon Crawler Carl", 1, "hardcover:2"));
        await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        Assert.True(await db.MediaItems.AnyAsync(m => m.Id == stub.Id));
        Assert.True(await db.UserLibraries.AnyAsync(u => u.MediaItemId == stub.Id));
    }

    [Fact]
    public async Task AManuallyPlacedRealBook_DoesNotCauseTheSlotsStubToBeRemoved()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = await SeriesWithHcIdAsync(db, author);
        var manual = Item(1, "Real Book", 1, author.Id); // the user pulled it OUT of the series
        var stub = Item(1, "Book", 2, series.Id, number: 1);
        stub.IsStub = true;
        db.MediaItems.AddRange(manual, stub);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = manual.Id, Source = "hardcover", ExternalId = "hardcover:2" },
            new MediaExternalId { MediaItemId = manual.Id, Source = "chronicle", ExternalId = "manual-series-member" },
            new MediaExternalId { MediaItemId = stub.Id, Source = "hardcover", ExternalId = "hardcover:1" });
        await db.SaveChangesAsync();

        var provider = Provider("Some Series", BookWithAlternates("hardcover:1", "Book", 1, "hardcover:2"));
        await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default);

        Assert.True(await db.MediaItems.AnyAsync(m => m.Id == stub.Id));
    }

    [Fact]
    public async Task NewStubs_GetEnrichmentRowsSeededForEverySupportingPlugin()
    {
        var (db, _, author) = await SetupAsync();
        await using var _ = db;
        var series = Item(1, "Some Series", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:1" });
        await db.SaveChangesAsync();

        var provider = Provider("Some Series", Book("hardcover:1", "Book One", 2001, 1));
        var otherPlugin = new Mock<IMetadataProvider>();
        otherPlugin.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "audiobooks" }]);
        var unsupportedPlugin = new Mock<IMetadataProvider>();
        unsupportedPlugin.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "movies" }]);
        var allProviders = new List<(string, IMetadataProvider)>
        {
            ("hardcover", provider.Object), ("chronicle.plugin.other", otherPlugin.Object),
            ("chronicle.plugin.unsupported", unsupportedPlugin.Object),
        };

        await NewService().EnsureSeriesStubsAsync(db, series, provider.Object, default, allProviders);

        var stub = await db.MediaItems.SingleAsync(m => m.Name == "Book One");
        var enrichmentPlugins = await db.MediaEnrichments.Where(e => e.MediaItemId == stub.Id).Select(e => e.PluginId).ToListAsync();
        Assert.Equal(["chronicle.plugin.other", "hardcover"], enrichmentPlugins.OrderBy(p => p));
    }
}
