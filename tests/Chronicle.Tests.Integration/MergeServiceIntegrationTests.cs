using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

public class MergeServiceIntegrationTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public MergeServiceIntegrationTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    [Fact]
    public async Task MergeAsync_PunctuationVariant_LoserDeletedAndAkaAdded()
    {
        int winnerId, loserId;

        // ── Seed ─────────────────────────────────────────────────────────────
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var mt = db.MediaTypes.First();

            var winner = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId    = mt.Id,
                Name           = "James S. A. Corey",
                NormalizedName = MediaItemNormalizer.NormalizeName("James S. A. Corey"),
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            }).Entity;
            var loser = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId    = mt.Id,
                Name           = "James S.A. Corey",
                NormalizedName = MediaItemNormalizer.NormalizeName("James S.A. Corey"),
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            }).Entity;
            db.SaveChanges();
            winnerId = winner.Id;
            loserId  = loser.Id;
        }

        // ── Act ───────────────────────────────────────────────────────────────
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IMergeService>();
            await svc.MergeAsync(winnerId, loserId, null);
        }

        // ── Assert ────────────────────────────────────────────────────────────
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            // Loser is gone
            db.MediaItems.Find(loserId).Should().BeNull();

            // Winner has an AKA  ("james sa corey" ≠ "james s a corey" so AKA required)
            var aliases = db.MediaItemAliases.Where(a => a.MediaItemId == winnerId).ToList();
            aliases.Should().ContainSingle(a => a.Alias == "James S.A. Corey" && a.Source == "merge");

            // Merge log recorded
            var log = db.MediaItemMerges.FirstOrDefault(m => m.WinnerId == winnerId);
            log.Should().NotBeNull();
            log!.LoserOriginalId.Should().Be(loserId);
            log.LoserName.Should().Be("James S.A. Corey");
        }
    }

    [Fact]
    public async Task MergeAsync_IdenticalNormalizedName_NoAkaAdded()
    {
        int winnerId, loserId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var mt = db.MediaTypes.First();

            var winner = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId    = mt.Id,
                Name           = "Abbey Road",
                NormalizedName = MediaItemNormalizer.NormalizeName("Abbey Road"),
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            }).Entity;
            var loser = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId    = mt.Id,
                Name           = "Abbey Road",
                NormalizedName = MediaItemNormalizer.NormalizeName("Abbey Road"),
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            }).Entity;
            db.SaveChanges();
            winnerId = winner.Id;
            loserId  = loser.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IMergeService>();
            await svc.MergeAsync(winnerId, loserId, null);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.MediaItems.Find(loserId).Should().BeNull();
            // Identical names → no AKA
            db.MediaItemAliases.Where(a => a.MediaItemId == winnerId).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task UnmergeAsync_RecreatesStubAndDeletesMergeLog()
    {
        int winnerId, mergeId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IMergeService>();
            var mt  = db.MediaTypes.First();

            var winner = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId    = mt.Id,
                Name           = "Brandon Sanderson Unmerge Test",
                NormalizedName = "brandon sanderson unmerge test",
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            }).Entity;
            var loser = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId    = mt.Id,
                Name           = "Brandon Sanderson Loser",
                NormalizedName = "brandon sanderson loser",
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            }).Entity;
            db.SaveChanges();
            winnerId = winner.Id;

            await svc.MergeAsync(winnerId, loser.Id, null);
            mergeId = db.MediaItemMerges.First(m => m.WinnerId == winnerId).Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IMergeService>();
            await svc.UnmergeAsync(mergeId);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            // Merge log should be deleted
            db.MediaItemMerges.Any(m => m.Id == mergeId).Should().BeFalse();

            // Stub with the loser name should exist
            db.MediaItems.Any(m => m.Name == "Brandon Sanderson Loser" && m.Id != winnerId)
                .Should().BeTrue();
        }
    }

    [Fact]
    public async Task MergeAsync_SameItemTwice_ThrowsInvalidOperation()
    {
        int itemId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var mt = db.MediaTypes.First();
            var item = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId    = mt.Id,
                Name           = "Self Merge Test",
                NormalizedName = "self merge test",
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            }).Entity;
            db.SaveChanges();
            itemId = item.Id;
        }

        using var scope2 = _factory.Services.CreateScope();
        var svc = scope2.ServiceProvider.GetRequiredService<IMergeService>();
        await svc.Invoking(s => s.MergeAsync(itemId, itemId, null))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different*");
    }
}
