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
/// A person merge the user made, taken apart by an earlier identity split (the article re-created as a new
/// record), is merged back -- and only when it is unmistakably that case.
/// </summary>
public class UserMergeRestoreServiceTests
{
    private static readonly DateTime MergedAt = new(2026, 10, 10, 3, 36, 0, DateTimeKind.Utc);
    private const string Article = "wikipedia:en:David_Fletcher_(musician)";

    private sealed record World(ChronicleDbContext Db, Mock<IMergeService> Merge, UserMergeRestoreService Svc, MediaItem Winner);

    private static async Task<World> BuildAsync(int? mergedBy = 1)
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "people", DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var winner = Person("David Fletcher", MergedAt.AddMonths(-1));
        db.MediaItems.Add(winner);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = winner.Id, Source = "tmdb", ExternalId = "tmdb:80828" });
        db.MediaItemMerges.Add(new MediaItemMerge
        {
            WinnerId = winner.Id, LoserOriginalId = 895049, LoserName = "David Fletcher", LoserMediaTypeId = 1,
            LoserExternalIdsJson = """[{"Source":"wikipedia","ExternalId":"wikipedia:en:David_Fletcher_(musician)","WasDuplicate":false}]""",
            MergedByUserId = mergedBy, MergedAt = MergedAt,
        });
        await db.SaveChangesAsync();

        var merge = new Mock<IMergeService>();
        var services = new ServiceCollection().AddSingleton(db).AddSingleton(merge.Object).BuildServiceProvider();
        var svc = new UserMergeRestoreService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UserMergeRestoreService>.Instance);
        return new World(db, merge, svc, winner);
    }

    private static MediaItem Person(string name, DateTime created) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = 0, CreatedAt = created, UpdatedAt = created,
    };

    /// <summary>What a split leaves behind: a newer record holding nothing but the article.</summary>
    private static async Task<MediaItem> AddSplitRecordAsync(ChronicleDbContext db, DateTime created, string article = Article)
    {
        var split = Person("David Fletcher", created);
        db.MediaItems.Add(split);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = split.Id, Source = "wikipedia", ExternalId = article });
        await db.SaveChangesAsync();
        return split;
    }

    [Fact]
    public async Task MergesTheSplitRecordBackIntoTheWinner_AsTheUsersOwnMerge()
    {
        var w = await BuildAsync(mergedBy: 7);
        await using var db = w.Db;
        var split = await AddSplitRecordAsync(db, MergedAt.AddDays(1));

        await w.Svc.ExecuteAsync(default);

        w.Merge.Verify(m => m.MergeAsync(w.Winner.Id, split.Id, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeavesAnAutomaticMergeAlone()
    {
        var w = await BuildAsync(mergedBy: null);
        await using var db = w.Db;
        await AddSplitRecordAsync(db, MergedAt.AddDays(1));

        await w.Svc.ExecuteAsync(default);

        w.Merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LeavesARecordThatPredatesTheMerge_ThatStillHoldsOtherIds_OrThatHasCredits()
    {
        var older = await BuildAsync();
        await using (older.Db) { await AddSplitRecordAsync(older.Db, MergedAt.AddDays(-1)); await older.Svc.ExecuteAsync(default); }
        older.Merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);

        var withIds = await BuildAsync();
        await using (withIds.Db)
        {
            var s = await AddSplitRecordAsync(withIds.Db, MergedAt.AddDays(1));
            withIds.Db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = s.Id, Source = "tmdb", ExternalId = "tmdb:1738197" });
            await withIds.Db.SaveChangesAsync();
            await withIds.Svc.ExecuteAsync(default);
        }
        withIds.Merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);

        var withCredits = await BuildAsync();
        await using (withCredits.Db)
        {
            var s = await AddSplitRecordAsync(withCredits.Db, MergedAt.AddDays(1));
            withCredits.Db.MediaCredits.Add(new MediaCredit { PersonMediaItemId = s.Id, MediaItemId = withCredits.Winner.Id, Role = "Actor", PersonName = "David Fletcher" });
            await withCredits.Db.SaveChangesAsync();
            await withCredits.Svc.ExecuteAsync(default);
        }
        withCredits.Merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DoesNothingWhileTheArticleIsStillOnTheWinner_OrOnMoreThanOneRecord()
    {
        var onWinner = await BuildAsync();
        await using (onWinner.Db)
        {
            onWinner.Db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = onWinner.Winner.Id, Source = "wikipedia", ExternalId = Article });
            await onWinner.Db.SaveChangesAsync();
            await onWinner.Svc.ExecuteAsync(default);
        }
        onWinner.Merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);

        var onTwo = await BuildAsync();
        await using (onTwo.Db)
        {
            await AddSplitRecordAsync(onTwo.Db, MergedAt.AddDays(1));
            await AddSplitRecordAsync(onTwo.Db, MergedAt.AddDays(2));
            await onTwo.Svc.ExecuteAsync(default);
        }
        onTwo.Merge.Verify(m => m.MergeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OneFailedMerge_DoesNotStopTheRest()
    {
        var w = await BuildAsync();
        await using var db = w.Db;
        var first = await AddSplitRecordAsync(db, MergedAt.AddDays(1));
        // a second user merge, with its own split record
        var winner2 = Person("Eric Leach", MergedAt.AddMonths(-1));
        db.MediaItems.Add(winner2);
        await db.SaveChangesAsync();
        db.MediaItemMerges.Add(new MediaItemMerge
        {
            WinnerId = winner2.Id, LoserOriginalId = 895054, LoserName = "Eric Leach", LoserMediaTypeId = 1,
            LoserExternalIdsJson = """[{"Source":"wikipedia","ExternalId":"wikipedia:en:Eric_Leach","WasDuplicate":false}]""",
            MergedByUserId = 1, MergedAt = MergedAt,
        });
        await db.SaveChangesAsync();
        var second = await AddSplitRecordAsync(db, MergedAt.AddDays(1), "wikipedia:en:Eric_Leach");
        w.Merge.Setup(m => m.MergeAsync(w.Winner.Id, first.Id, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await w.Svc.ExecuteAsync(default);

        w.Merge.Verify(m => m.MergeAsync(winner2.Id, second.Id, 1, It.IsAny<CancellationToken>()), Times.Once);
    }
}
