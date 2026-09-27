using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Live (2026-09-27): William Hertling's "Singularity" series books sat under Jeremy Robinson's BOOK
/// "Singularity" -- a book under a book, under the wrong author.
/// </summary>
public class MisparentedChildRepairServiceTests
{
    private static async Task<(ChronicleDbContext db, MisparentedChildRepairService svc)> SetupAsync()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.AddRange(
            new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow },
            new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        return (db, new MisparentedChildRepairService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MisparentedChildRepairService>.Instance));
    }

    private static MediaItem Item(string name, int level, int? parentId, string? folder = null) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = level, ParentId = parentId,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        MetadataJson = folder is null ? null
            : "{\"fileScanner\":{\"folderPath\":\"" + folder.Replace("\\", "\\\\") + "\"}}",
    };

    [Fact]
    public async Task ABookUnderAnotherBook_MovesUnderTheSameNamedSeriesOfItsOwnAuthor()
    {
        var (db, svc) = await SetupAsync();
        await using var _ = db;
        var robinson = Item("Jeremy Robinson", 0, null, @"E:\Audio Books\Jeremy Robinson");
        var hertling = Item("William Hertling", 0, null, @"E:\Audio Books\William Hertling");
        db.MediaItems.AddRange(robinson, hertling);
        await db.SaveChangesAsync();
        var infinite = Item("Infinite", 1, robinson.Id);
        var singularitySeries = Item("Singularity", 1, hertling.Id);
        db.MediaItems.AddRange(infinite, singularitySeries);
        await db.SaveChangesAsync();
        var singularityBook = Item("Singularity", 2, infinite.Id, @"E:\Audio Books\Jeremy Robinson\Infinite - 13 - (2022) - Singularity");
        db.MediaItems.Add(singularityBook);
        await db.SaveChangesAsync();
        var wrong = Item("A.I. Apocalypse", 2, singularityBook.Id, @"E:\Audio Books\William Hertling\Singularity - 2 - (2012) - A.I. Apocalypse");
        db.MediaItems.Add(wrong);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Equal(singularitySeries.Id, (await db.MediaItems.FindAsync(wrong.Id))!.ParentId);
        Assert.Equal(infinite.Id, (await db.MediaItems.FindAsync(singularityBook.Id))!.ParentId); // the real book stays put
    }

    [Fact]
    public async Task WithoutAMatchingAuthorFolderOrSeries_NothingIsGuessed()
    {
        var (db, svc) = await SetupAsync();
        await using var _ = db;
        var author = Item("Author", 0, null, @"E:\Audio Books\Author");
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("Other Series", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        var book = Item("Book", 2, series.Id, @"E:\Audio Books\Author\Other Series - 1 - Book");
        db.MediaItems.Add(book);
        await db.SaveChangesAsync();
        var child = Item("Child", 2, book.Id, @"E:\Audio Books\Author\Child");   // wrong parent name has no series under this author
        db.MediaItems.Add(child);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Equal(book.Id, (await db.MediaItems.FindAsync(child.Id))!.ParentId);
    }

    [Fact]
    public async Task CorrectlyParentedBooks_AndFlatTypes_AreNeverTouched()
    {
        var (db, svc) = await SetupAsync();
        await using var _ = db;
        var author = Item("Author", 0, null, @"E:\Audio Books\Author");
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("Series", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        var book = Item("Book", 2, series.Id, @"E:\Audio Books\Author\Series - 1 - Book");
        db.MediaItems.Add(book);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Equal(series.Id, (await db.MediaItems.FindAsync(book.Id))!.ParentId);
    }
}
