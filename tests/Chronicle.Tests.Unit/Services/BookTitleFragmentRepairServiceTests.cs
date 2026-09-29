using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Live (2026-09-27): Hardcover's own sync gave the book's series position only inside the title
/// ("He Who Fights with Monsters #4", no separate series field), so every sync minted a standalone
/// duplicate beside the already-scanned series.
/// </summary>
public class BookTitleFragmentRepairServiceTests
{
    private static (ChronicleDbContext db, BookTitleFragmentRepairService svc) Setup()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();

        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddScoped<IMergeService>(_ => new MergeService(
                db, Mock.Of<IMetadataResolutionService>(), Mock.Of<IMovieCollectionService>(),
                Mock.Of<IFileScanService>(), NullLogger<MergeService>.Instance))
            .BuildServiceProvider();
        return (db, new BookTitleFragmentRepairService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<BookTitleFragmentRepairService>.Instance));
    }

    private static MediaItem Item(string name, int level, int? parentId, int? number = null, double? position = null) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = level, ParentId = parentId, Number = number, SeriesPosition = position,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task AFragmentMatchingAnAlreadyScannedBook_MergesIntoIt_AndKeepsItsExternalId()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Shirtaloon", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("He Who Fights with Monsters", 1, author.Id);
        var fragment = Item("He Who Fights with Monsters #4", 1, author.Id);
        db.MediaItems.AddRange(series, fragment);
        await db.SaveChangesAsync();
        var scanned = Item("He Who Fights with Monsters 4", 2, series.Id, number: 4);
        db.MediaItems.Add(scanned);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = fragment.Id, Source = "hardcover", ExternalId = "hardcover:series:186536" });
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Null(await db.MediaItems.FindAsync(fragment.Id));
        Assert.Contains(await db.MediaExternalIds.Where(e => e.MediaItemId == scanned.Id).Select(e => e.ExternalId).ToListAsync(),
            id => id == "hardcover:series:186536");
    }

    [Fact]
    public async Task AFragmentWithNoScannedCounterpart_BecomesThatNumberedBookInTheSeries()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("Some Series", 1, author.Id);
        var fragment = Item("Some Series #12", 1, author.Id);
        db.MediaItems.AddRange(series, fragment);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        var moved = (await db.MediaItems.FindAsync(fragment.Id))!;
        Assert.Equal((series.Id, 2, 12, "Some Series"), (moved.ParentId, moved.HierarchyLevel, moved.Number, moved.Name));
    }

    [Fact]
    public async Task WithNoSiblingSeries_TheStandaloneTitleIsLeftAlone()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var lone = Item("Catch-22", 1, author.Id); // no "#N" -- SplitSeriesTag returns no number
        db.MediaItems.Add(lone);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        var still = (await db.MediaItems.FindAsync(lone.Id))!;
        Assert.Equal((author.Id, 1), (still.ParentId, still.HierarchyLevel));
    }

    [Fact]
    public async Task AFractionalTitlePosition_ReachesSeriesPosition_WhileNumberStaysFloored()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("The Expanse", 1, author.Id);
        var fragment = Item("The Expanse #1.1", 1, author.Id);
        db.MediaItems.AddRange(series, fragment);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        var moved = (await db.MediaItems.FindAsync(fragment.Id))!;
        Assert.Equal((series.Id, 1, 1.1), (moved.ParentId, moved.Number, moved.SeriesPosition));
    }

    [Fact]
    public async Task AFragmentReplacesAStalePositionTheStubAlreadyCarried()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("Some Series", 1, author.Id);
        var fragment = Item("Some Series #12", 1, author.Id, number: 3, position: 3.5); // leftover from an old placement
        db.MediaItems.AddRange(series, fragment);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        var moved = (await db.MediaItems.FindAsync(fragment.Id))!;
        Assert.Equal((12, 12.0), (moved.Number, moved.SeriesPosition));
    }

    [Fact]
    public async Task AFractionalFragment_IsNotMergedIntoTheWholeNumberedBookSharingItsFlooredNumber()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("The Expanse", 1, author.Id);
        var fragment = Item("The Expanse #1.1", 1, author.Id);
        db.MediaItems.AddRange(series, fragment);
        await db.SaveChangesAsync();
        var leviathan = Item("Leviathan Wakes", 2, series.Id, number: 1, position: 1);
        db.MediaItems.Add(leviathan);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.NotNull(await db.MediaItems.FindAsync(leviathan.Id));
        var moved = (await db.MediaItems.FindAsync(fragment.Id))!; // survived as its own book, not merged away
        Assert.Equal((series.Id, 2, 1.1), (moved.ParentId, moved.HierarchyLevel, moved.SeriesPosition));
    }

    [Fact]
    public async Task AWholeNumberedFragment_StillMergesIntoAScannedBookWhoseSeriesPositionIsUnset()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("Series", 1, author.Id);
        var fragment = Item("Series #4", 1, author.Id);
        db.MediaItems.AddRange(series, fragment);
        await db.SaveChangesAsync();
        var scanned = Item("Series 4", 2, series.Id, number: 4); // legacy row: Number only
        db.MediaItems.Add(scanned);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Null(await db.MediaItems.FindAsync(fragment.Id));
    }
}
