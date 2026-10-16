using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Chronicle.Tests.Unit.Services;

/// <summary>Merging items of two media types: the loser's own type is what the log records, and children of a type the winner cannot hold are refused.</summary>
public class MergeServiceCrossTypeTests
{
    private const int PeopleType = 1;
    private const int AudiobookType = 2;

    private static (ChronicleDbContext db, MergeService svc) Setup()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        db.MediaTypes.Add(new MediaType { Id = PeopleType, Name = "people", IsPersonType = true, DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        db.MediaTypes.Add(new MediaType { Id = AudiobookType, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var svc = new MergeService(db, Mock.Of<IMetadataResolutionService>(), Mock.Of<IMovieCollectionService>(),
            Mock.Of<IFileScanService>(), NullLogger<MergeService>.Instance);
        return (db, svc);
    }

    private static MediaItem Item(int typeId, string name, int level = 0, int? parentId = null) => new()
    {
        MediaTypeId = typeId, Name = name, HierarchyLevel = level, ParentId = parentId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task MergingAChildlessItemOfAnotherType_LogsItsOwnType_SoAnUnmergeRestoresIt()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var person = Item(PeopleType, "Iain M. Banks");
        var author = Item(AudiobookType, "Iain M. Banks");
        db.MediaItems.AddRange(person, author);
        await db.SaveChangesAsync();

        await svc.MergeAsync(person.Id, author.Id, mergedByUserId: null);

        var log = await db.MediaItemMerges.SingleAsync();
        log.LoserMediaTypeId.Should().Be(AudiobookType);

        await svc.UnmergeAsync(log.Id);
        var restored = await db.MediaItems.SingleAsync(m => m.Id != person.Id);
        restored.MediaTypeId.Should().Be(AudiobookType, "the restored item is the audiobook author again, not a person");
    }

    [Fact]
    public async Task AnItemWithChildrenOfItsOwnType_CannotBeAbsorbedByAnItemOfAnotherType()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var person = Item(PeopleType, "Iain M. Banks");
        var author = Item(AudiobookType, "Iain M. Banks");
        db.MediaItems.AddRange(person, author);
        await db.SaveChangesAsync();
        var series = Item(AudiobookType, "Culture", level: 1, parentId: author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();

        var act = () => svc.MergeAsync(person.Id, author.Id, mergedByUserId: null);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("different media type");
        (await db.MediaItems.FindAsync(series.Id))!.ParentId.Should().Be(author.Id, "nothing moved");
    }

    [Fact]
    public async Task ChildrenOfTheWinnersOwnType_MayFollowTheMerge()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var winner = Item(AudiobookType, "Iain M. Banks");
        var loser = Item(PeopleType, "Iain M. Banks");
        db.MediaItems.AddRange(winner, loser);
        await db.SaveChangesAsync();
        var series = Item(AudiobookType, "Culture", level: 1, parentId: loser.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();

        await svc.MergeAsync(winner.Id, loser.Id, mergedByUserId: null);

        (await db.MediaItems.FindAsync(series.Id))!.ParentId.Should().Be(winner.Id);
    }
}
