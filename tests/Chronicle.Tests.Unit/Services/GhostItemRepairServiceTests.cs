using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A renamed folder ("Hell Divers Series - 1 - ..." becoming "Hell Divers - 1 - ...") is imported again under its new
/// name, and the old item keeps only a name and a number once its file link is cleared. Those leftovers are removed --
/// and only those.
/// </summary>
public class GhostItemRepairServiceTests
{
    private const string Ghost = """{"fileScanner":{"importedAt":"2026-08-18T00:07:12Z","filePaths":[],"folderPath":null}}""";
    private const string WithFile = """{"fileScanner":{"filePaths":["E:/Audio Books/X/Real Book"],"folderPath":"E:/Audio Books/X/Real Book"}}""";

    private sealed record World(ChronicleDbContext Db, MediaItem Author, MediaItem OldSeries, List<MediaItem> OldBooks);

    private static async Task<World> BuildAsync()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.AddRange(
            new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, HierarchyLabels = "Author,Series,Book", CreatedAt = DateTime.UtcNow },
            new MediaType { Id = 2, Name = "music", DisplayName = "Music", HierarchyLevels = 3, HierarchyLabels = "Artist,Album,Track", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var author = Item("Nicholas Sansbury Smith", 0, null, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var oldSeries = Item("Hell Divers Series", 1, author.Id, Ghost);
        db.MediaItems.Add(oldSeries);
        await db.SaveChangesAsync();
        var books = Enumerable.Range(1, 3).Select(n => Item("Hell Divers Series", 2, oldSeries.Id, Ghost, number: n)).ToList();
        db.MediaItems.AddRange(books);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = oldSeries.Id, Source = "hardcover", ExternalId = "hardcover:series:198266" });
        await db.SaveChangesAsync();
        return new World(db, author, oldSeries, books);
    }

    private static MediaItem Item(string name, int level, int? parentId, string? json, int? number = null, int typeId = 1) => new()
    {
        MediaTypeId = typeId, Name = name, HierarchyLevel = level, ParentId = parentId, MetadataJson = json, Number = number,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static GhostItemRepairService NewService(ChronicleDbContext db) =>
        new(new ServiceCollection().AddSingleton(db).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GhostItemRepairService>.Instance);

    [Fact]
    public async Task RemovesTheGhostBooksAndTheSeriesTheyEmptied()
    {
        var w = await BuildAsync();
        await using var db = w.Db;

        await NewService(db).ExecuteAsync(default);

        Assert.Empty(await db.MediaItems.Where(m => m.ParentId == w.OldSeries.Id).ToListAsync());
        Assert.Null(await db.MediaItems.FindAsync(w.OldSeries.Id));                       // the series they emptied
        Assert.NotNull(await db.MediaItems.FindAsync(w.Author.Id));                       // the author stays
        Assert.Empty(await db.MediaExternalIds.Where(e => e.MediaItemId == w.OldSeries.Id).ToListAsync());
    }

    [Fact]
    public async Task KeepsAnythingThatHoldsAFileAnIdCompletedMetadataOrTheUsersMark()
    {
        var w = await BuildAsync();
        await using var db = w.Db;
        var withFile = Item("Has A File", 2, w.OldSeries.Id, WithFile, number: 10);
        var withId = Item("Has An Id", 2, w.OldSeries.Id, Ghost, number: 11);
        var completed = Item("Has Metadata", 2, w.OldSeries.Id, Ghost, number: 12);
        var rated = Item("Was Rated", 2, w.OldSeries.Id, Ghost, number: 13);
        db.MediaItems.AddRange(withFile, withId, completed, rated);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = withId.Id, Source = "hardcover", ExternalId = "hardcover:1" });
        db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = completed.Id, PluginId = "hardcover", Status = EnrichmentStatus.Completed, MaxRetries = 3 });
        db.UserLibraries.Add(new UserLibrary { UserId = 1, MediaItemId = rated.Id, Status = LibraryStatus.Completed, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await NewService(db).ExecuteAsync(default);

        foreach (var kept in new[] { withFile, withId, completed, rated })
            Assert.NotNull(await db.MediaItems.FindAsync(kept.Id));
        Assert.NotNull(await db.MediaItems.FindAsync(w.OldSeries.Id));                    // it still has books, so it stays
        Assert.Equal(4, await db.MediaItems.CountAsync(m => m.ParentId == w.OldSeries.Id));
    }

    [Fact]
    public async Task LeavesABookThePlaceUserPlacedInASeriesByHand()
    {
        var w = await BuildAsync();
        await using var db = w.Db;
        var manual = Item("Placed By Hand", 2, w.OldSeries.Id, Ghost, number: 20);
        db.MediaItems.Add(manual);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = manual.Id, Source = "chronicle", ExternalId = "manual-series-member" });
        await db.SaveChangesAsync();

        await NewService(db).ExecuteAsync(default);

        Assert.NotNull(await db.MediaItems.FindAsync(manual.Id));
    }

    [Fact]
    public async Task OnlyLooksAtTypesWhoseSecondLevelIsASeries()
    {
        var w = await BuildAsync();
        await using var db = w.Db;
        var artist = Item("An Artist", 0, null, null, typeId: 2);
        db.MediaItems.Add(artist);
        await db.SaveChangesAsync();
        var album = Item("An Album With Its Folder Gone", 1, artist.Id, Ghost, typeId: 2);
        db.MediaItems.Add(album);
        await db.SaveChangesAsync();

        await NewService(db).ExecuteAsync(default);

        Assert.NotNull(await db.MediaItems.FindAsync(album.Id));                          // music is not touched
    }
}
