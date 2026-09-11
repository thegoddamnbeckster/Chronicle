using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class NfoGenerationServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChronicleDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(opts);
    }

    /// <summary>Same "real ServiceCollection so CreateScope() actually works" pattern as
    /// ScheduledScanServiceTests -- NfoGenerationService creates its own outer scope, plus one
    /// more scope per item it processes.</summary>
    private static IServiceScopeFactory MakeScopeFactory(
        ChronicleDbContext db, INfoRebuildQueueService rebuildQueue, INfoPushService nfoPush)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(rebuildQueue);
        services.AddSingleton(nfoPush);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task<ChronicleDbContext> WithOneUserAsync(ChronicleDbContext db)
    {
        db.Users.Add(new User
        {
            Id = 1, Username = "testuser", PasswordHash = "h",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static NfoRebuildQueueItem PendingRow(int id, int mediaItemId) => new()
    {
        Id = id, MediaItemId = mediaItemId, Kind = "movie", EnqueuedAt = DateTime.UtcNow,
    };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoUsersYet_NeverQueriesTheQueue()
    {
        var db = MakeDb(); // no users seeded
        var rebuildQueue = new Mock<INfoRebuildQueueService>();
        var nfoPush = new Mock<INfoPushService>();

        var service = new NfoGenerationService(MakeScopeFactory(db, rebuildQueue.Object, nfoPush.Object));
        await service.ExecuteAsync(CancellationToken.None);

        rebuildQueue.Verify(
            q => q.GetPendingForGenerationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NothingPending_NeverCallsTryPush()
    {
        var db = await WithOneUserAsync(MakeDb());
        var rebuildQueue = new Mock<INfoRebuildQueueService>();
        rebuildQueue.Setup(q => q.GetPendingForGenerationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var nfoPush = new Mock<INfoPushService>();

        var service = new NfoGenerationService(MakeScopeFactory(db, rebuildQueue.Object, nfoPush.Object));
        await service.ExecuteAsync(CancellationToken.None);

        nfoPush.Verify(
            p => p.TryPushAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulPush_CompletesTheQueueRowDirectly()
    {
        var db = await WithOneUserAsync(MakeDb());
        var rebuildQueue = new Mock<INfoRebuildQueueService>();
        rebuildQueue.Setup(q => q.GetPendingForGenerationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([PendingRow(id: 42, mediaItemId: 100)]);
        var nfoPush = new Mock<INfoPushService>();
        nfoPush.Setup(p => p.TryPushAsync(100, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = new NfoGenerationService(MakeScopeFactory(db, rebuildQueue.Object, nfoPush.Object));
        await service.ExecuteAsync(CancellationToken.None);

        // Completed directly by NfoGenerationService itself -- no kodiDeviceId, no device claim.
        rebuildQueue.Verify(q => q.CompleteFromGenerationAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_FailedPush_LeavesTheRowPendingForTheNextRun()
    {
        var db = await WithOneUserAsync(MakeDb());
        var rebuildQueue = new Mock<INfoRebuildQueueService>();
        rebuildQueue.Setup(q => q.GetPendingForGenerationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([PendingRow(id: 42, mediaItemId: 100)]);
        var nfoPush = new Mock<INfoPushService>();
        nfoPush.Setup(p => p.TryPushAsync(100, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // an attempt was made and failed (e.g. sidecar fetch failed)

        var service = new NfoGenerationService(MakeScopeFactory(db, rebuildQueue.Object, nfoPush.Object));
        await service.ExecuteAsync(CancellationToken.None);

        rebuildQueue.Verify(
            q => q.CompleteFromGenerationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NullOutcomeButMediaItemDeleted_CompletesTheRowSoItStopsBeingRetried()
    {
        var db = await WithOneUserAsync(MakeDb());
        // Deliberately no MediaItem with id 100 -- simulates the item having been deleted/merged
        // away since this row was queued.
        var rebuildQueue = new Mock<INfoRebuildQueueService>();
        rebuildQueue.Setup(q => q.GetPendingForGenerationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([PendingRow(id: 42, mediaItemId: 100)]);
        var nfoPush = new Mock<INfoPushService>();
        nfoPush.Setup(p => p.TryPushAsync(100, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null); // "item not found" is one of TryPushAsync's own null cases

        var service = new NfoGenerationService(MakeScopeFactory(db, rebuildQueue.Object, nfoPush.Object));
        await service.ExecuteAsync(CancellationToken.None);

        rebuildQueue.Verify(q => q.CompleteFromGenerationAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NullOutcomeButMediaItemStillExists_LeavesTheRowPending()
    {
        var db = await WithOneUserAsync(MakeDb());
        db.MediaItems.Add(new MediaItem
        {
            Id = 100, Name = "Not Yet Locatable", MediaTypeId = 1, HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var rebuildQueue = new Mock<INfoRebuildQueueService>();
        rebuildQueue.Setup(q => q.GetPendingForGenerationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([PendingRow(id: 42, mediaItemId: 100)]);
        var nfoPush = new Mock<INfoPushService>();
        nfoPush.Setup(p => p.TryPushAsync(100, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null); // e.g. "no known on-disk location yet"

        var service = new NfoGenerationService(MakeScopeFactory(db, rebuildQueue.Object, nfoPush.Object));
        await service.ExecuteAsync(CancellationToken.None);

        // The item genuinely still exists -- a later pass (once its location resolves) should
        // still get a chance at it, not have this row silently discarded.
        rebuildQueue.Verify(
            q => q.CompleteFromGenerationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_MultiplePendingItems_PushesEveryOneOfThem()
    {
        var db = await WithOneUserAsync(MakeDb());
        var rows = Enumerable.Range(1, 5).Select(i => PendingRow(id: i, mediaItemId: 100 + i)).ToList();
        var rebuildQueue = new Mock<INfoRebuildQueueService>();
        rebuildQueue.Setup(q => q.GetPendingForGenerationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);
        var nfoPush = new Mock<INfoPushService>();
        // Every item succeeds -- deliberately avoids the null-outcome path here, which touches
        // ChronicleDbContext concurrently across parallel items; that path is covered above with
        // a single item instead, where concurrency isn't in play.
        nfoPush.Setup(p => p.TryPushAsync(It.IsAny<int>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = new NfoGenerationService(MakeScopeFactory(db, rebuildQueue.Object, nfoPush.Object));
        await service.ExecuteAsync(CancellationToken.None);

        foreach (var row in rows)
            nfoPush.Verify(p => p.TryPushAsync(row.MediaItemId, 1, It.IsAny<CancellationToken>()), Times.Once);
        rebuildQueue.Verify(
            q => q.CompleteFromGenerationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
    }
}
