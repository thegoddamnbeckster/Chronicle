using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A book's fractional series position (Hardcover's 1.1) lives in MediaItem.SeriesPosition beside the
/// floored Number. A merge must not silently drop it: it is recorded in the merge log so Unmerge can
/// restore it, and the file-identity self-heal that copies the loser's Number must copy it with it.
/// </summary>
public class MergeServiceSeriesPositionTests
{
    private static (ChronicleDbContext db, MergeService svc) Setup()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // UnmergeAsync wraps itself in a transaction, which the in-memory provider ignores.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var svc = new MergeService(
            db, Mock.Of<IMetadataResolutionService>(), Mock.Of<IMovieCollectionService>(),
            Mock.Of<IFileScanService>(), NullLogger<MergeService>.Instance);
        return (db, svc);
    }

    private static MediaItem Book(string name, int parentId, int? number, double? position, string? metadataJson = null) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = 2, ParentId = parentId, Number = number, SeriesPosition = position,
        MetadataJson = metadataJson, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static async Task<MediaItem> SeriesAsync(ChronicleDbContext db)
    {
        var author = new MediaItem { MediaTypeId = 1, Name = "James S. A. Corey", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = new MediaItem { MediaTypeId = 1, Name = "The Expanse", HierarchyLevel = 1, ParentId = author.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        return series;
    }

    [Fact]
    public async Task Merge_RecordsTheLosersPreciseSeriesPosition_InTheMergeLog()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var series = await SeriesAsync(db);
        var winner = Book("The Butcher of Anderson Station", series.Id, number: 1, position: 1.1);
        var loser  = Book("The Butcher of Anderson Station", series.Id, number: 1, position: 1.1);
        db.MediaItems.AddRange(winner, loser);
        await db.SaveChangesAsync();

        await svc.MergeLoadedItemsAsync(db, winner, loser, null);
        await db.SaveChangesAsync();

        var log = await db.MediaItemMerges.SingleAsync();
        Assert.Equal((1, 1.1), (log.LoserNumber, log.LoserSeriesPosition));
    }

    [Fact]
    public async Task Unmerge_RestoresTheFractionalPositionOntoTheRecreatedStub_NotJustTheFlooredNumber()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var series = await SeriesAsync(db);
        var winner = Book("Leviathan Wakes", series.Id, number: 1, position: 1);
        var loser  = Book("The Butcher of Anderson Station", series.Id, number: 1, position: 1.1);
        db.MediaItems.AddRange(winner, loser);
        await db.SaveChangesAsync();
        await svc.MergeLoadedItemsAsync(db, winner, loser, null);
        await db.SaveChangesAsync();

        await svc.UnmergeAsync((await db.MediaItemMerges.SingleAsync()).Id);

        var stub = await db.MediaItems.SingleAsync(m => m.Name == "The Butcher of Anderson Station");
        Assert.Equal((1, 1.1), (stub.Number, stub.SeriesPosition));
    }

    [Fact]
    public async Task Unmerge_OfAMergeLoggedWithNoPrecisePosition_LeavesTheStubsSeriesPositionNull()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var series = await SeriesAsync(db);
        var winner = Book("Leviathan Wakes", series.Id, number: 1, position: null);
        var loser  = Book("Leviathan Wakes (dup)", series.Id, number: 1, position: null);
        db.MediaItems.AddRange(winner, loser);
        await db.SaveChangesAsync();
        await svc.MergeLoadedItemsAsync(db, winner, loser, null);
        await db.SaveChangesAsync();

        await svc.UnmergeAsync((await db.MediaItemMerges.SingleAsync()).Id);

        var stub = await db.MediaItems.SingleAsync(m => m.Name == "Leviathan Wakes (dup)");
        Assert.Equal(1, stub.Number);
        Assert.Null(stub.SeriesPosition);
    }

    [Fact]
    public async Task FileIdentitySelfHeal_CopiesTheLosersSeriesPosition_WithItsNumber()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var series = await SeriesAsync(db);
        // The winner's own recorded file is some other book's; the loser's file really is this title's.
        var winner = Book("The Butcher of Anderson Station", series.Id, number: 9, position: 9.5,
            metadataJson: "{\"fileScanner\":{\"filePaths\":[\"/books/Some Other Book.m4b\"]}}");
        var loser = Book("Butcher stub", series.Id, number: 1, position: 1.1,
            metadataJson: "{\"fileScanner\":{\"filePaths\":[\"/books/The Butcher of Anderson Station.m4b\"]}}");
        db.MediaItems.AddRange(winner, loser);
        await db.SaveChangesAsync();

        await svc.MergeLoadedItemsAsync(db, winner, loser, null);
        await db.SaveChangesAsync();

        // Number and SeriesPosition move together -- the winner's old 9.5 must not outlive its old 9.
        Assert.Equal((1, 1.1), (winner.Number, winner.SeriesPosition));
    }

    [Fact]
    public async Task FileIdentitySelfHeal_WithALoserThatHasNoPrecisePosition_ClearsTheWinnersStaleOne()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var series = await SeriesAsync(db);
        var winner = Book("The Butcher of Anderson Station", series.Id, number: 9, position: 9.5,
            metadataJson: "{\"fileScanner\":{\"filePaths\":[\"/books/Some Other Book.m4b\"]}}");
        var loser = Book("Butcher stub", series.Id, number: 2, position: null,
            metadataJson: "{\"fileScanner\":{\"filePaths\":[\"/books/The Butcher of Anderson Station.m4b\"]}}");
        db.MediaItems.AddRange(winner, loser);
        await db.SaveChangesAsync();

        await svc.MergeLoadedItemsAsync(db, winner, loser, null);
        await db.SaveChangesAsync();

        Assert.Equal(2, winner.Number);
        Assert.Null(winner.SeriesPosition);
    }
}
