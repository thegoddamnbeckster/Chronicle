using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class NfoRebuildQueueServiceTests : IDisposable
{
    private readonly ChronicleDbContext _db;
    private readonly NfoRebuildQueueService _svc;

    private const int MovieTypeId = 1;
    private const int TvTypeId    = 2;

    public NfoRebuildQueueServiceTests()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new ChronicleDbContext(opts);
        _svc = new NfoRebuildQueueService(_db, Mock.Of<ILogger<NfoRebuildQueueService>>());

        // Bypasses the process-static seed throttle -- see ResetSeedThrottleForTests' own doc.
        NfoRebuildQueueService.ResetSeedThrottleForTests();

        _db.MediaTypes.Add(new MediaType { Id = MovieTypeId, Name = "movies", DisplayName = "Movies", CreatedAt = DateTime.UtcNow });
        _db.MediaTypes.Add(new MediaType { Id = TvTypeId,    Name = "tv",     DisplayName = "TV",      CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ClaimBatchAsync_SeedsAndClaimsAMovie()
    {
        _db.MediaItems.Add(new MediaItem
        {
            Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0,
        });
        await _db.SaveChangesAsync();

        var claimed = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(5));

        claimed.Items.Should().ContainSingle();
        claimed.Items[0].MediaItemId.Should().Be(100);
        claimed.Items[0].Kind.Should().Be("movie");
        claimed.Items[0].Name.Should().Be("Alien");
        claimed.Items[0].Year.Should().Be(1979);
        claimed.TotalPending.Should().Be(1);
    }

    [Fact]
    public async Task ClaimBatchAsync_EpisodeIncludesParentShowNameAndYear()
    {
        _db.MediaItems.Add(new MediaItem { Id = 200, MediaTypeId = TvTypeId, Name = "Lanterns", Year = 2026, HierarchyLevel = 0 });
        _db.MediaItems.Add(new MediaItem { Id = 201, MediaTypeId = TvTypeId, Name = "Season 1", ParentId = 200, Number = 1, HierarchyLevel = 1 });
        _db.MediaItems.Add(new MediaItem { Id = 202, MediaTypeId = TvTypeId, Name = "OutKast", ParentId = 201, Number = 3, HierarchyLevel = 2 });
        await _db.SaveChangesAsync();

        var claimed = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(5));

        // Show (level 0) and episode (level 2) are both queued; the season container (level 1) is not.
        claimed.Items.Should().HaveCount(2);
        claimed.TotalPending.Should().Be(2);
        var episode = claimed.Items.Should().ContainSingle(c => c.Kind == "episode").Subject;
        episode.MediaItemId.Should().Be(202);
        episode.Season.Should().Be(1);
        episode.Episode.Should().Be(3);
        episode.ShowName.Should().Be("Lanterns");
        episode.ShowYear.Should().Be(2026);

        var show = claimed.Items.Should().ContainSingle(c => c.Kind == "tvshow").Subject;
        show.MediaItemId.Should().Be(200);
    }

    [Fact]
    public async Task ClaimBatchAsync_DoesNotReturnAlreadyCompletedItems()
    {
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0 });
        await _db.SaveChangesAsync();

        var firstBatch = await _svc.ClaimBatchAsync(1, 10, TimeSpan.FromMinutes(5));
        await _svc.CompleteAsync(firstBatch.Items[0].QueueItemId, kodiDeviceId: 1);

        var secondBatch = await _svc.ClaimBatchAsync(2, 10, TimeSpan.FromMinutes(5));

        secondBatch.Items.Should().BeEmpty();
        secondBatch.TotalPending.Should().Be(0);
    }

    [Fact]
    public async Task ClaimBatchAsync_DoesNotDoubleClaimWithinAnotherDevicesActiveLease()
    {
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0 });
        await _db.SaveChangesAsync();

        var deviceAClaim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        deviceAClaim.Items.Should().ContainSingle();

        var deviceBClaim = await _svc.ClaimBatchAsync(kodiDeviceId: 2, batchSize: 10, TimeSpan.FromMinutes(10));

        deviceBClaim.Items.Should().BeEmpty("device 2 must not claim an item still within device 1's active lease");
    }

    [Fact]
    public async Task ClaimBatchAsync_ReclaimsAfterLeaseExpires()
    {
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0 });
        await _db.SaveChangesAsync();

        // Negative lease = already expired the instant it's set, simulating a device that
        // claimed and then vanished (crashed, lost network) before ever completing/releasing.
        var deviceAClaim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromSeconds(-1));
        deviceAClaim.Items.Should().ContainSingle();

        var deviceBClaim = await _svc.ClaimBatchAsync(kodiDeviceId: 2, batchSize: 10, TimeSpan.FromMinutes(10));

        deviceBClaim.Items.Should().ContainSingle("device 1's lapsed lease must free the item up for another device");
    }

    [Fact]
    public async Task CompleteAsync_SticksEvenWhenCallerIsNotTheCurrentClaimant()
    {
        // Deliberately NOT gated on current ownership -- see CompleteAsync's own doc for why a
        // late-but-genuine completion (e.g. this device's own lease lapsed and another device
        // re-claimed the same row in the meantime) must still be accepted rather than silently
        // discarded, which would leave a correctly-rebuilt item stuck being redone forever.
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0 });
        await _db.SaveChangesAsync();

        var claim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));

        // Device 2 never held this claim, yet its completion must still stick.
        await _svc.CompleteAsync(claim.Items[0].QueueItemId, kodiDeviceId: 2);

        var row = await _db.NfoRebuildQueue.FindAsync(claim.Items[0].QueueItemId);
        row!.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CompleteAsync_IsIdempotentOnceAlreadyCompleted()
    {
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0 });
        await _db.SaveChangesAsync();

        var claim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        await _svc.CompleteAsync(claim.Items[0].QueueItemId, kodiDeviceId: 1);
        var firstCompletedAt = (await _db.NfoRebuildQueue.FindAsync(claim.Items[0].QueueItemId))!.CompletedAt;

        // A second, later completion call (e.g. a slow duplicate request) must not re-stamp the
        // timestamp or otherwise disturb an already-completed row.
        await _svc.CompleteAsync(claim.Items[0].QueueItemId, kodiDeviceId: 2);

        var row = await _db.NfoRebuildQueue.FindAsync(claim.Items[0].QueueItemId);
        row!.CompletedAt.Should().Be(firstCompletedAt);
    }

    [Fact]
    public async Task ReleaseAsync_MakesItemImmediatelyClaimableByAnotherDevice()
    {
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0 });
        await _db.SaveChangesAsync();

        var deviceAClaim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        await _svc.ReleaseAsync(deviceAClaim.Items[0].QueueItemId, kodiDeviceId: 1);

        var deviceBClaim = await _svc.ClaimBatchAsync(kodiDeviceId: 2, batchSize: 10, TimeSpan.FromMinutes(10));

        deviceBClaim.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task ClaimBatchAsync_NeverQueuesACollectionContainer()
    {
        // A collection container is movie-typed at HierarchyLevel 0, identical to a real
        // standalone movie by every column the type/level filter alone can see -- confirmed
        // live (2026-09-06) that this leaked every container into the queue, where it could
        // never resolve to a real local file. Identified the same way
        // MetadataEnrichmentService.MarkCollectionContainersPendingAsSkippedAsync does: a
        // "collection:" external id, not a real per-provider match.
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0 });
        _db.MediaItems.Add(new MediaItem { Id = 300, MediaTypeId = MovieTypeId, Name = "Toy Story Collection", HierarchyLevel = 0 });
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = 300, Source = "chronicle", ExternalId = "collection:300" });
        await _db.SaveChangesAsync();

        var claimed = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(5));

        claimed.Items.Should().ContainSingle();
        claimed.Items[0].MediaItemId.Should().Be(100);
    }

    [Fact]
    public async Task ClaimBatchAsync_PrunesACollectionContainerAlreadyQueuedBeforeTheFix()
    {
        // Simulates a row that was seeded before the collection-container exclusion existed --
        // EnsureSeededAsync's next throttled pass must clean it up, not just avoid adding new
        // ones, so an already-installed deployment's existing backlog is also fixed.
        _db.MediaItems.Add(new MediaItem { Id = 300, MediaTypeId = MovieTypeId, Name = "Toy Story Collection", HierarchyLevel = 0 });
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = 300, Source = "chronicle", ExternalId = "collection:300" });
        _db.NfoRebuildQueue.Add(new NfoRebuildQueueItem { MediaItemId = 300, Kind = "movie", EnqueuedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        NfoRebuildQueueService.ResetSeedThrottleForTests();
        var claimed = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(5));

        claimed.Items.Should().BeEmpty();
        claimed.TotalPending.Should().Be(0);
        (await _db.NfoRebuildQueue.AnyAsync(q => q.MediaItemId == 300)).Should().BeFalse();
    }

    [Fact]
    public async Task ReseedAllAsync_ResetsCompletedItemsBackToPending()
    {
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0 });
        await _db.SaveChangesAsync();

        var claim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        await _svc.CompleteAsync(claim.Items[0].QueueItemId, kodiDeviceId: 1);

        var pending = await _svc.ReseedAllAsync();

        pending.Should().Be(1);
        var reclaim = await _svc.ClaimBatchAsync(kodiDeviceId: 2, batchSize: 10, TimeSpan.FromMinutes(10));
        reclaim.Items.Should().ContainSingle("a force-reseed must make the already-completed item claimable again");
    }
}
