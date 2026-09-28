using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class MediaServiceTests
{
    private static ChronicleDbContext NewDb() => new(new DbContextOptionsBuilder<ChronicleDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static MediaService NewService(ChronicleDbContext db) =>
        new(db, Mock.Of<IPluginRegistry>());

    [Fact]
    public async Task GetChildrenAsync_OrdersByThePreciseSeriesPositionWhenPresent_NotJustTheFlooredNumber()
    {
        // Root-caused live (2026-09-29): a book-series companion novella at Hardcover position 1.1
        // and the next full novel at position 2 both had Number 1 vs 2 as usual, but a novella at
        // 1.1 sitting between two Number-1 books (e.g. after a manual reorder) needs SeriesPosition,
        // not Number, to sort correctly. Ordering by Number alone can't tell 1 and 1.1 apart at all.
        var db = NewDb();
        await using var _ = db;
        var mt = new MediaType { Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(mt);
        var series = new MediaItem { MediaTypeId = 1, Name = "Series", HierarchyLevel = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();

        var bookTwo      = new MediaItem { MediaTypeId = 1, Name = "Book Two", HierarchyLevel = 2, ParentId = series.Id, Number = 2, SeriesPosition = 2.0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var novella       = new MediaItem { MediaTypeId = 1, Name = "Novella 1.1", HierarchyLevel = 2, ParentId = series.Id, Number = 1, SeriesPosition = 1.1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var bookOne      = new MediaItem { MediaTypeId = 1, Name = "Book One", HierarchyLevel = 2, ParentId = series.Id, Number = 1, SeriesPosition = 1.0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.AddRange(bookTwo, novella, bookOne); // deliberately out of order
        await db.SaveChangesAsync();

        var children = await NewService(db).GetChildrenAsync(series.Id);

        Assert.Equal(["Book One", "Novella 1.1", "Book Two"], children.Select(c => c.Name));
    }

    [Fact]
    public async Task GetChildrenAsync_FallsBackToNumber_WhenSeriesPositionIsNull()
    {
        // Every media type other than a book-series book never sets SeriesPosition -- ordering
        // must be unaffected for them (TV seasons, movie collection members, tracks, etc.).
        var db = NewDb();
        await using var _ = db;
        var mt = new MediaType { Name = "tv", DisplayName = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(mt);
        var show = new MediaItem { MediaTypeId = 1, Name = "Show", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(show);
        await db.SaveChangesAsync();

        var season2 = new MediaItem { MediaTypeId = 1, Name = "Season 2", HierarchyLevel = 1, ParentId = show.Id, Number = 2, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var season1 = new MediaItem { MediaTypeId = 1, Name = "Season 1", HierarchyLevel = 1, ParentId = show.Id, Number = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.AddRange(season2, season1);
        await db.SaveChangesAsync();

        var children = await NewService(db).GetChildrenAsync(show.Id);

        Assert.Equal(["Season 1", "Season 2"], children.Select(c => c.Name));
    }
}
