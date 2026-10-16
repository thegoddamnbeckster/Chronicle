using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// "Prey" and "Next" by Michael Crichton are standalone books. A provider matched each to a same-named series by
/// ANOTHER author, the series sync built that series' books underneath as placeholders, and the book showed their
/// covers as its own. A book that holds a file and has only placeholders under it is turned back into a book.
/// </summary>
public class MisattachedSeriesRepairServiceTests
{
    private const string BookFile = """{"fileScanner":{"filePaths":["E:/Audio Books/Michael Crichton/- - (2004) - Prey"],"folderPath":"E:/Audio Books/Michael Crichton/- - (2004) - Prey"},"hardcover":{"title":"Prey","posterUrl":"https://hc/x.jpg"}}""";

    private sealed record World(ChronicleDbContext Db, MediaItem Author, MediaItem Book, Mock<IMetadataResolutionService> Resolution);

    private static ChronicleDbContext NewDb()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return db;
    }

    private static MediaItem Item(string name, int level, int? parentId, bool stub = false, string? json = null) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = level, ParentId = parentId, IsStub = stub, MetadataJson = json,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>A standalone book (file) carrying another author's series, with four placeholder "books" under it.</summary>
    private static async Task<World> BuildAsync(string? bookJson = BookFile)
    {
        var db = NewDb();
        var author = Item("Michael Crichton", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var book = Item("Prey", 1, author.Id, json: bookJson);
        book.PosterUrl = "https://hc/x.jpg";
        db.MediaItems.Add(book);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = book.Id, Source = "hardcover", ExternalId = "hardcover:series:91366" },
            new MediaExternalId { MediaItemId = book.Id, Source = "openlibrary", ExternalId = "OL46883W" });
        db.MediaEnrichments.AddRange(
            new MediaItemEnrichment { MediaItemId = book.Id, PluginId = "hardcover", Status = EnrichmentStatus.Completed, MaxRetries = 3 },
            new MediaItemEnrichment { MediaItemId = book.Id, PluginId = "openlibrary", Status = EnrichmentStatus.Completed, MaxRetries = 3 });
        foreach (var title in new[] { "Thirst", "Lust", "Appetite", "Forgive Me for I Have Sinned" })
        {
            var child = Item(title, 2, book.Id, stub: true);
            db.MediaItems.Add(child);
            await db.SaveChangesAsync();
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = child.Id, Source = "hardcover", ExternalId = "hardcover:" + title.Length });
        }
        await db.SaveChangesAsync();
        return new World(db, author, book, new Mock<IMetadataResolutionService>());
    }

    private static MisattachedSeriesRepairService NewService(World w) =>
        new(new ServiceCollection().AddSingleton(w.Db).AddSingleton(w.Resolution.Object).BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MisattachedSeriesRepairService>.Instance);

    [Fact]
    public async Task TurnsAFileHoldingBookWithOnlyPlaceholdersBackIntoABook()
    {
        var w = await BuildAsync();
        await using var db = w.Db;

        await NewService(w).ExecuteAsync(default);

        Assert.Empty(await db.MediaItems.Where(m => m.ParentId == w.Book.Id).ToListAsync());      // the other author's books are gone
        var ids = (await db.MediaExternalIds.Where(e => e.MediaItemId == w.Book.Id).ToListAsync()).Select(e => e.ExternalId).ToList();
        Assert.DoesNotContain("hardcover:series:91366", ids);                                      // the wrong series id is gone
        Assert.Contains("OL46883W", ids);                                                           // its own work id stays
        var book = (await db.MediaItems.FindAsync(w.Book.Id))!;
        Assert.Null(book.PosterUrl);                                                                // the borrowed cover goes
        Assert.DoesNotContain("posterUrl", book.MetadataJson!);                                     // along with what the series stored
        Assert.Contains("fileScanner", book.MetadataJson!);                                         // the file link is untouched
        Assert.All(await db.MediaEnrichments.Where(e => e.MediaItemId == w.Book.Id).ToListAsync(),
            e => Assert.Equal(EnrichmentStatus.Pending, e.Status));                                 // looked up again, as a book
        w.Resolution.Verify(r => r.ResolveAsync(It.Is<MediaItem>(m => m.Id == w.Book.Id), It.IsAny<ChronicleDbContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeavesARealSeriesAlone_ThatHasNoFileOfItsOwn()
    {
        var w = await BuildAsync(bookJson: """{"fileScanner":{"filePaths":[]}}""");
        await using var db = w.Db;

        await NewService(w).ExecuteAsync(default);

        Assert.Equal(4, await db.MediaItems.CountAsync(m => m.ParentId == w.Book.Id));
    }

    [Fact]
    public async Task LeavesABookThatHasARealChildOrAChildTheUserHasDataOn()
    {
        var withRealChild = await BuildAsync();
        await using (withRealChild.Db)
        {
            withRealChild.Db.MediaItems.Add(Item("My Own Nested Folder", 2, withRealChild.Book.Id, stub: false,
                json: """{"fileScanner":{"filePaths":["E:/x/y"]}}"""));
            await withRealChild.Db.SaveChangesAsync();
            await NewService(withRealChild).ExecuteAsync(default);
            Assert.Equal(5, await withRealChild.Db.MediaItems.CountAsync(m => m.ParentId == withRealChild.Book.Id));
        }

        var withData = await BuildAsync();
        await using (withData.Db)
        {
            var child = await withData.Db.MediaItems.FirstAsync(m => m.ParentId == withData.Book.Id);
            withData.Db.UserLibraries.Add(new UserLibrary { UserId = 1, MediaItemId = child.Id, Status = LibraryStatus.Completed, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await withData.Db.SaveChangesAsync();
            await NewService(withData).ExecuteAsync(default);
            Assert.Equal(4, await withData.Db.MediaItems.CountAsync(m => m.ParentId == withData.Book.Id));
        }
    }

    [Fact]
    public async Task TheSeriesSync_NeverBuildsASeriesUnderAFileHoldingBookWithoutRealChildren()
    {
        var w = await BuildAsync();
        await using var db = w.Db;
        // start from a book with no children yet, as "Prey" was before the series sync ran
        db.MediaItems.RemoveRange(db.MediaItems.Where(m => m.ParentId == w.Book.Id));
        await db.SaveChangesAsync();
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.Name).Returns("hardcover");
        provider.Setup(p => p.GetSupportedMediaTypes()).Returns([new MediaTypeSupport { MediaTypeName = "audiobooks" }]);
        provider.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata
            {
                Title = "Prey",
                Results = [new MediaMetadata
                {
                    ExternalId = "hardcover:1", Source = "hardcover", Title = "Thirst",
                    ExtendedData = JsonSerializer.SerializeToElement(new { seriesPosition = 1.0 }),
                }],
            });

        var ok = await new BookSeriesService(NullLogger<BookSeriesService>.Instance)
            .EnsureSeriesStubsAsync(db, w.Book, provider.Object, default);

        Assert.True(ok);
        Assert.Empty(await db.MediaItems.Where(m => m.ParentId == w.Book.Id).ToListAsync());
    }
}
