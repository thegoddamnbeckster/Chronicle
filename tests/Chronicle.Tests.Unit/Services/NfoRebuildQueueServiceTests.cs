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
        // Same reasoning for the LastSeenAt update throttle -- process-static and keyed only by
        // kodiDeviceId, so a test reusing a device id another test just claimed with inside the
        // last simulated minute would otherwise see its own LastSeenAt update silently skipped.
        NfoRebuildQueueService.ResetLastSeenThrottleForTests();

        _db.MediaTypes.Add(new MediaType { Id = MovieTypeId, Name = "movies", DisplayName = "Movies", CreatedAt = DateTime.UtcNow });
        _db.MediaTypes.Add(new MediaType { Id = TvTypeId,    Name = "tv",     DisplayName = "TV",      CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // EnsureSeededAsync now requires a real physical file before it will queue anything (see its
    // own doc, 2026-09-08) -- every seeded movie/episode in these tests needs this MetadataJson
    // shape (Chronicle.Services.Scan.FileIdentityJson.HasKnownFile's own doc) or it's silently
    // excluded, exactly as intended for a metadata-only stub.
    private static string FileJson(string name) => "{\"fileScanner\":{\"filePaths\":[\"X:\\\\fake\\\\" + name + ".mkv\"]}}";

    [Fact]
    public async Task ClaimBatchAsync_SeedsAndClaimsAMovie()
    {
        _db.MediaItems.Add(new MediaItem
        {
            Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0,
            MetadataJson = FileJson("Alien"),
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
    public async Task ClaimBatchAsync_ExcludeKinds_OmitsThatKindFromTheBatch()
    {
        // Per-user report (2026-09-08): a device whose local library covers only a fraction of
        // the shared catalog was claiming, failing to resolve, and releasing tens of thousands
        // of episodes in a single run before this existed -- once Chronicle_Scraper's own
        // per-kind failure-streak tracking (nfo_rebuild.py) decides it can't resolve a kind this
        // run, it should stop being handed more of it.
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        _db.MediaItems.Add(new MediaItem { Id = 200, MediaTypeId = TvTypeId, Name = "Lanterns", Year = 2026, HierarchyLevel = 0 });
        _db.MediaItems.Add(new MediaItem { Id = 201, MediaTypeId = TvTypeId, Name = "Season 1", ParentId = 200, Number = 1, HierarchyLevel = 1 });
        _db.MediaItems.Add(new MediaItem { Id = 202, MediaTypeId = TvTypeId, Name = "OutKast", ParentId = 201, Number = 3, HierarchyLevel = 2, MetadataJson = FileJson("OutKast") });
        await _db.SaveChangesAsync();

        var claimed = await _svc.ClaimBatchAsync(
            kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(5), excludeKinds: ["episode"]);

        // Show (level 0, kind "tvshow") and movie both still come back -- only "episode" itself
        // was excluded, proving the filter is precise to the requested kind(s), not "any TV content".
        claimed.Items.Should().HaveCount(2);
        claimed.Items.Should().Contain(c => c.Kind == "movie");
        claimed.Items.Should().Contain(c => c.Kind == "tvshow");
        claimed.Items.Should().NotContain(c => c.Kind == "episode");
    }

    [Fact]
    public async Task ClaimBatchAsync_EpisodeIncludesParentShowNameAndYear()
    {
        _db.MediaItems.Add(new MediaItem { Id = 200, MediaTypeId = TvTypeId, Name = "Lanterns", Year = 2026, HierarchyLevel = 0 });
        _db.MediaItems.Add(new MediaItem { Id = 201, MediaTypeId = TvTypeId, Name = "Season 1", ParentId = 200, Number = 1, HierarchyLevel = 1 });
        _db.MediaItems.Add(new MediaItem { Id = 202, MediaTypeId = TvTypeId, Name = "OutKast", ParentId = 201, Number = 3, HierarchyLevel = 2, MetadataJson = FileJson("OutKast") });
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
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
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
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        var deviceAClaim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        deviceAClaim.Items.Should().ContainSingle();

        var deviceBClaim = await _svc.ClaimBatchAsync(kodiDeviceId: 2, batchSize: 10, TimeSpan.FromMinutes(10));

        deviceBClaim.Items.Should().BeEmpty("device 2 must not claim an item still within device 1's active lease");
    }

    [Fact]
    public async Task ClaimBatchAsync_ReclaimsAfterLeaseExpires()
    {
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
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
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
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
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
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
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        var deviceAClaim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        await _svc.ReleaseAsync(deviceAClaim.Items[0].QueueItemId, kodiDeviceId: 1);

        var deviceBClaim = await _svc.ClaimBatchAsync(kodiDeviceId: 2, batchSize: 10, TimeSpan.FromMinutes(10));

        deviceBClaim.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task ReleaseAsync_PreventsTheSameDeviceFromImmediatelyReclaimingTheReleasedItem()
    {
        // Root-caused live (2026-09-07): a device whose local library is a strict subset of the
        // shared catalog would claim an item, fail to find it locally, release it -- and then
        // immediately reclaim that SAME item on its very next batch, since a plain release fully
        // clears ClaimedByKodiDeviceId with no memory of who just tried and failed. For a device
        // stuck at the front of a long contiguous run of items it can never find, this spun
        // forever on the same handful of rows instead of ever reaching new ones.
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        var claim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        await _svc.ReleaseAsync(claim.Items[0].QueueItemId, kodiDeviceId: 1);

        var reclaim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));

        reclaim.Items.Should().BeEmpty("the same device that just released this item must not immediately reclaim it");
    }

    [Fact]
    public async Task ReleaseAsync_StillLetsADifferentDeviceClaimImmediately()
    {
        // The cooldown is specific to the device that released -- a device that actually HAS
        // the file locally must not be made to wait out someone else's cooldown.
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        var claim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        await _svc.ReleaseAsync(claim.Items[0].QueueItemId, kodiDeviceId: 1);

        var otherDeviceClaim = await _svc.ClaimBatchAsync(kodiDeviceId: 2, batchSize: 10, TimeSpan.FromMinutes(10));

        otherDeviceClaim.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task ClaimBatchAsync_LetsTheSameDeviceReclaimOnceTheReleaseCooldownHasPassed()
    {
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        var claim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        await _svc.ReleaseAsync(claim.Items[0].QueueItemId, kodiDeviceId: 1);

        // Back-dates the release past the cooldown window directly (rather than waiting or
        // mocking the clock) -- simulates the device's library having had time to change (a
        // rescan, a new mount) since it last gave up on this item.
        var row = await _db.NfoRebuildQueue.FindAsync(claim.Items[0].QueueItemId);
        row!.LastReleasedAt = DateTime.UtcNow.AddHours(-3);
        await _db.SaveChangesAsync();

        var reclaim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));

        reclaim.Items.Should().ContainSingle("the cooldown has elapsed, so the original device may try again");
    }

    [Fact]
    public async Task ReseedAllAsync_ClearsAnyStandingReleaseCooldown()
    {
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        var claim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        await _svc.ReleaseAsync(claim.Items[0].QueueItemId, kodiDeviceId: 1);

        // A force-reseed means "everyone retry everything" -- a standing cooldown from before
        // the reseed must not keep blocking the device that hit it.
        await _svc.ReseedAllAsync();

        var reclaim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));

        reclaim.Items.Should().ContainSingle();
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
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        _db.MediaItems.Add(new MediaItem { Id = 300, MediaTypeId = MovieTypeId, Name = "Toy Story Collection", HierarchyLevel = 0, MetadataJson = FileJson("Toy Story Collection") });
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
        _db.MediaItems.Add(new MediaItem { Id = 300, MediaTypeId = MovieTypeId, Name = "Toy Story Collection", HierarchyLevel = 0, MetadataJson = FileJson("Toy Story Collection") });
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
    public async Task ClaimBatchAsync_NeverQueuesAMovieWithNoPhysicalFile()
    {
        // Per-user report (2026-09-08): only items with a real, scanned file anywhere should
        // ever enter this queue -- a metadata-only stub (imported, scrobbled, or on a want-to-
        // watch list with no local file) can never be resolved by ANY Kodi device, so queuing it
        // just wastes a claim/release cycle and, worse, can trip nfo_rebuild.py's own
        // consecutive-failure kind-exclusion breaker for a completely unrelated reason.
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        _db.MediaItems.Add(new MediaItem { Id = 400, MediaTypeId = MovieTypeId, Name = "Vaporware", Year = 2026, HierarchyLevel = 0 });
        await _db.SaveChangesAsync();

        var claimed = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(5));

        claimed.Items.Should().ContainSingle();
        claimed.Items[0].MediaItemId.Should().Be(100);
    }

    [Fact]
    public async Task ClaimBatchAsync_NeverQueuesAShowWithNoFileBearingEpisode()
    {
        // A show container never carries file info of its own (only a descendant episode does),
        // so this can't be a plain "does this MediaItem have a file" check -- it has to roll the
        // check up from the episode level. A show with only metadata-only episodes underneath it
        // (e.g. seen by a metadata provider but never actually scanned from disk) must not queue
        // the show either, for the same reason a file-less movie must not.
        _db.MediaItems.Add(new MediaItem { Id = 200, MediaTypeId = TvTypeId, Name = "Lanterns", Year = 2026, HierarchyLevel = 0 });
        _db.MediaItems.Add(new MediaItem { Id = 201, MediaTypeId = TvTypeId, Name = "Season 1", ParentId = 200, Number = 1, HierarchyLevel = 1 });
        _db.MediaItems.Add(new MediaItem { Id = 202, MediaTypeId = TvTypeId, Name = "OutKast", ParentId = 201, Number = 3, HierarchyLevel = 2 });
        await _db.SaveChangesAsync();

        var claimed = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(5));

        claimed.Items.Should().BeEmpty();
        claimed.TotalPending.Should().Be(0);
    }

    [Fact]
    public async Task ClaimBatchAsync_PrunesAFileLessRowAlreadyQueuedBeforeTheFix()
    {
        // Same "clean up the existing backlog once" reasoning as
        // ClaimBatchAsync_PrunesACollectionContainerAlreadyQueuedBeforeTheFix -- an
        // already-installed deployment's queue may already carry thousands of file-less rows
        // seeded before this check existed; EnsureSeededAsync's next throttled pass must remove
        // them, not just avoid adding new ones.
        _db.MediaItems.Add(new MediaItem { Id = 400, MediaTypeId = MovieTypeId, Name = "Vaporware", Year = 2026, HierarchyLevel = 0 });
        _db.NfoRebuildQueue.Add(new NfoRebuildQueueItem { MediaItemId = 400, Kind = "movie", EnqueuedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        NfoRebuildQueueService.ResetSeedThrottleForTests();
        var claimed = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(5));

        claimed.Items.Should().BeEmpty();
        claimed.TotalPending.Should().Be(0);
        (await _db.NfoRebuildQueue.AnyAsync(q => q.MediaItemId == 400)).Should().BeFalse();
    }

    [Fact]
    public async Task ReseedAllAsync_ResetsCompletedItemsBackToPending()
    {
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        var claim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        await _svc.CompleteAsync(claim.Items[0].QueueItemId, kodiDeviceId: 1);

        var pending = await _svc.ReseedAllAsync();

        pending.Should().Be(1);
        var reclaim = await _svc.ClaimBatchAsync(kodiDeviceId: 2, batchSize: 10, TimeSpan.FromMinutes(10));
        reclaim.Items.Should().ContainSingle("a force-reseed must make the already-completed item claimable again");
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsOverallCounts()
    {
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        _db.MediaItems.Add(new MediaItem { Id = 101, MediaTypeId = MovieTypeId, Name = "Aliens", Year = 1986, HierarchyLevel = 0, MetadataJson = FileJson("Aliens") });
        _db.MediaItems.Add(new MediaItem { Id = 102, MediaTypeId = MovieTypeId, Name = "Alien 3", Year = 1992, HierarchyLevel = 0, MetadataJson = FileJson("Alien 3") });
        await _db.SaveChangesAsync();

        var claim = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        claim.Items.Should().HaveCount(3);
        await _svc.CompleteAsync(claim.Items[0].QueueItemId, kodiDeviceId: 1);

        var status = await _svc.GetStatusAsync();

        status.TotalItems.Should().Be(3);
        status.CompletedCount.Should().Be(1);
        status.PendingCount.Should().Be(2);
        status.ActiveClaimCount.Should().Be(2, "the other two items are still claimed with an active lease");
    }

    [Fact]
    public async Task GetStatusAsync_SeedsTheQueueItselfWithoutRequiringAPriorClaimCall()
    {
        // No ClaimBatchAsync call anywhere in this test -- simulates a fresh install, or one
        // where no Kodi device has ever polled yet. A status check must still reflect the real
        // library instead of reading as "nothing to rebuild" just because nobody's claimed
        // anything.
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        var status = await _svc.GetStatusAsync();

        status.TotalItems.Should().Be(1);
        status.PendingCount.Should().Be(1);
        status.CompletedCount.Should().Be(0);
    }

    [Fact]
    public async Task GetStatusAsync_BreaksDownPerDeviceAndResolvesDeviceNames()
    {
        _db.KodiDevices.Add(new KodiDevice { Id = 1, UserId = 1, ApiTokenId = 1, Name = "Vision", Host = "10.0.0.162", Port = 8080, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
        _db.KodiDevices.Add(new KodiDevice { Id = 2, UserId = 1, ApiTokenId = 2, Name = "Office", Host = "10.0.0.163", Port = 8080, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        _db.MediaItems.Add(new MediaItem { Id = 101, MediaTypeId = MovieTypeId, Name = "Aliens", Year = 1986, HierarchyLevel = 0, MetadataJson = FileJson("Aliens") });
        await _db.SaveChangesAsync();

        var claimVision = await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 1, TimeSpan.FromMinutes(10));
        await _svc.CompleteAsync(claimVision.Items[0].QueueItemId, kodiDeviceId: 1);
        var claimOffice = await _svc.ClaimBatchAsync(kodiDeviceId: 2, batchSize: 1, TimeSpan.FromMinutes(10));

        var status = await _svc.GetStatusAsync();

        status.Devices.Should().HaveCount(2);
        var vision = status.Devices.Should().ContainSingle(d => d.KodiDeviceId == 1).Subject;
        vision.DeviceName.Should().Be("Vision");
        vision.Host.Should().Be("10.0.0.162");
        vision.CompletedCount.Should().Be(1);
        vision.ActiveClaims.Should().Be(0);
        var office = status.Devices.Should().ContainSingle(d => d.KodiDeviceId == 2).Subject;
        office.DeviceName.Should().Be("Office");
        office.Host.Should().Be("10.0.0.163");
        office.CompletedCount.Should().Be(0);
        office.ActiveClaims.Should().Be(1);
    }

    [Fact]
    public async Task ClaimBatchAsync_RefreshesTheDevicesLastSeenAt()
    {
        // Per-user report (2026-09-09): "Kodi downstairs has been off for over an hour" with no
        // way to tell from this panel -- it only ever showed lifetime completed counts, which
        // don't change whether a device is on or off right now. KodiDevice.LastSeenAt's only
        // other writer is the addon's own 6-hourly re-registration ping, far too coarse to be a
        // meaningful liveness signal on its own -- claiming a batch only happens while the
        // device's background rebuild service is actually running, so refreshing it here too
        // gives GetStatusAsync something fresh to show.
        var staleLastSeen = DateTime.UtcNow.AddHours(-3);
        _db.KodiDevices.Add(new KodiDevice
        {
            Id = 1, UserId = 1, ApiTokenId = 1, Name = "Downstairs", Host = "10.2.0.2", Port = 8080,
            CreatedAt = DateTime.UtcNow, LastSeenAt = staleLastSeen,
        });
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));

        var device = await _db.KodiDevices.FindAsync(1);
        device!.LastSeenAt.Should().BeAfter(staleLastSeen).And.BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetStatusAsync_SurfacesTheRefreshedLastSeenAtOnEachDevice()
    {
        // Same scenario as ClaimBatchAsync_RefreshesTheDevicesLastSeenAt, but asserting through
        // the actual API-facing DTO (what the rebuild queue panel reads) rather than just the
        // raw DB row -- the two aren't the same thing until GetStatusAsync's own device mapping
        // is confirmed to carry the field through.
        var staleLastSeen = DateTime.UtcNow.AddHours(-3);
        _db.KodiDevices.Add(new KodiDevice
        {
            Id = 1, UserId = 1, ApiTokenId = 1, Name = "Downstairs", Host = "10.2.0.2", Port = 8080,
            CreatedAt = DateTime.UtcNow, LastSeenAt = staleLastSeen,
        });
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));

        var status = await _svc.GetStatusAsync();

        var device = status.Devices.Should().ContainSingle(d => d.KodiDeviceId == 1).Subject;
        device.LastSeenAt.Should().NotBeNull()
            .And.Subject.Should().BeAfter(staleLastSeen).And.BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNullLastSeenAtForADeletedDevice()
    {
        // Mirrors GetStatusAsync_FallsBackToPlaceholderNameForADeletedDevice's own scenario --
        // a device row can be deleted/re-registered independently of the rebuild queue (see
        // NfoRebuildQueueItem's own doc on why there's no FK), so LastSeenAt must degrade to
        // null gracefully rather than throwing on the missing lookup.
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();
        await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));

        var status = await _svc.GetStatusAsync();

        var device = status.Devices.Should().ContainSingle().Subject;
        device.LastSeenAt.Should().BeNull();
    }

    [Fact]
    public async Task ClaimBatchAsync_DoesNotRewriteLastSeenAtOnEveryCall_WithinTheThrottleWindow()
    {
        // An actively-rebuilding device calls ClaimBatchAsync repeatedly in quick succession --
        // writing LastSeenAt on every single one would be a wasted SELECT+UPDATE per call for a
        // liveness display that doesn't need sub-minute precision. See LastSeenUpdateThrottle's
        // own doc.
        _db.KodiDevices.Add(new KodiDevice
        {
            Id = 1, UserId = 1, ApiTokenId = 1, Name = "Downstairs", Host = "10.2.0.2", Port = 8080,
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.MinValue,
        });
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        var firstSeenAt = (await _db.KodiDevices.FindAsync(1))!.LastSeenAt;

        // Second claim moments later -- well within the 1-minute throttle window.
        await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));
        var secondSeenAt = (await _db.KodiDevices.FindAsync(1))!.LastSeenAt;

        secondSeenAt.Should().Be(firstSeenAt, "a second claim inside the throttle window should not re-write LastSeenAt");
    }

    [Fact]
    public async Task ClaimBatchAsync_ForANonexistentDevice_DoesNotThrow()
    {
        // A device row can be deleted/re-registered independently of the rebuild queue (see
        // NfoRebuildQueueItem's own doc on why there's no FK) -- the LastSeenAt refresh must be
        // a no-op for an unknown device id, not a failure that blocks the actual claim.
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        var claim = await _svc.ClaimBatchAsync(kodiDeviceId: 999, batchSize: 10, TimeSpan.FromMinutes(10));

        claim.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task GetStatusAsync_DisambiguatesTwoDevicesThatShareTheSameSelfReportedName()
    {
        // Root-caused live (2026-09-07): KodiDevice.Name has no uniqueness constraint and is
        // entirely self-reported by each Kodi instance's own addon settings -- a device
        // mis-registered under a stale/copy-pasted name (here, Vision registering itself as
        // "Kodi upstairs", the SAME name the real upstairs Shield already uses) looked, in the
        // UI, indistinguishable from a bug ("this device is listed twice, and Vision is
        // missing") rather than what it actually was. Host is what a caller needs to tell them
        // apart even though DeviceName alone can't.
        _db.KodiDevices.Add(new KodiDevice { Id = 1, UserId = 1, ApiTokenId = 1, Name = "Kodi upstairs", Host = "10.0.0.229", Port = 8080, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
        _db.KodiDevices.Add(new KodiDevice { Id = 2, UserId = 1, ApiTokenId = 2, Name = "Kodi upstairs", Host = "10.0.0.162", Port = 8080, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        _db.MediaItems.Add(new MediaItem { Id = 101, MediaTypeId = MovieTypeId, Name = "Aliens", Year = 1986, HierarchyLevel = 0, MetadataJson = FileJson("Aliens") });
        await _db.SaveChangesAsync();

        await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 1, TimeSpan.FromMinutes(10));
        await _svc.ClaimBatchAsync(kodiDeviceId: 2, batchSize: 1, TimeSpan.FromMinutes(10));

        var status = await _svc.GetStatusAsync();

        status.Devices.Should().HaveCount(2, "these are two genuinely different devices, not one duplicated");
        status.Devices.Should().OnlyHaveUniqueItems(d => d.KodiDeviceId);
        status.Devices.Select(d => d.DeviceName).Should().AllBe("Kodi upstairs");
        status.Devices.Select(d => d.Host).Should().BeEquivalentTo(["10.0.0.229", "10.0.0.162"],
            "identical names must still be distinguishable by host");
    }

    [Fact]
    public async Task GetStatusAsync_DoesNotCountALapsedLeaseAsAnActiveClaim()
    {
        _db.KodiDevices.Add(new KodiDevice { Id = 1, UserId = 1, ApiTokenId = 1, Name = "Vision", Host = "10.0.0.162", Port = 8080, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();

        // Negative lease = already expired -- simulates a device that claimed and vanished
        // before completing or releasing, same setup as ClaimBatchAsync_ReclaimsAfterLeaseExpires.
        await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromSeconds(-1));

        var status = await _svc.GetStatusAsync();

        status.ActiveClaimCount.Should().Be(0, "a lapsed lease is claimable again, not actively in-flight");
        status.PendingCount.Should().Be(1, "the item is still outstanding overall, just not credited to anyone's active claim");
        // Vision has neither an active claim nor a completion right now, so it's dropped from
        // the per-device breakdown entirely (GetStatusAsync's own "nothing to show" filter) --
        // rather than showing a device with two zero columns for no reason.
        status.Devices.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatusAsync_FallsBackToPlaceholderNameForADeletedDevice()
    {
        // No KodiDevices row for id 1 at all -- simulates a device deleted/re-registered after
        // claiming (see the nfo_rebuild_queue table's own mapping comment on why there's
        // deliberately no FK enforcing this can't happen).
        _db.MediaItems.Add(new MediaItem { Id = 100, MediaTypeId = MovieTypeId, Name = "Alien", Year = 1979, HierarchyLevel = 0, MetadataJson = FileJson("Alien") });
        await _db.SaveChangesAsync();
        await _svc.ClaimBatchAsync(kodiDeviceId: 1, batchSize: 10, TimeSpan.FromMinutes(10));

        var status = await _svc.GetStatusAsync();

        var device = status.Devices.Should().ContainSingle().Subject;
        device.DeviceName.Should().Be("(deleted device)");
        device.Host.Should().BeNull();
        device.ActiveClaims.Should().Be(1);
    }
}
