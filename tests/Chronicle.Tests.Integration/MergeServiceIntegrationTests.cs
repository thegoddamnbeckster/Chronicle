using Chronicle.Core.Helpers;
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
    public async Task UnmergeAsync_SharedExternalId_RestoresStubCopyWithoutStealingWinnersOwnId()
    {
        int winnerId, loserId, mergeId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IMergeService>();
            var mt  = db.MediaTypes.First();

            var winner = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId    = mt.Id,
                Name           = "Shared Ext Id Winner",
                NormalizedName = "shared ext id winner",
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            }).Entity;
            var loser = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId    = mt.Id,
                Name           = "Shared Ext Id Loser",
                NormalizedName = "shared ext id loser",
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            }).Entity;
            db.SaveChanges();
            winnerId = winner.Id;
            loserId  = loser.Id;

            // Both sides already own an identical (Source, ExternalId) row before the merge —
            // this is the collision case Pass 2 targets.
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = winnerId, Source = "tmdb", ExternalId = "603" });
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = loserId,  Source = "tmdb", ExternalId = "603" });
            db.SaveChanges();

            await svc.MergeAsync(winnerId, loserId, null);
            mergeId = db.MediaItemMerges.First(m => m.WinnerId == winnerId).Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            // Merge deletes the loser's duplicate row and keeps the winner's own row untouched.
            db.MediaExternalIds.Count(e => e.MediaItemId == winnerId && e.Source == "tmdb" && e.ExternalId == "603")
                .Should().Be(1);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IMergeService>();
            await svc.UnmergeAsync(mergeId);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            // The winner must still have its own pre-merge external ID — it must NOT have been
            // stolen and handed to the restored stub.
            db.MediaExternalIds.Count(e => e.MediaItemId == winnerId && e.Source == "tmdb" && e.ExternalId == "603")
                .Should().Be(1);

            // The restored stub gets its own fresh copy instead.
            var stub = db.MediaItems.First(m => m.Name == "Shared Ext Id Loser" && m.Id != winnerId);
            db.MediaExternalIds.Count(e => e.MediaItemId == stub.Id && e.Source == "tmdb" && e.ExternalId == "603")
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task MergeAsync_LoserWasPreviousMergeWinner_RepointsPriorMergeLogInsteadOfCascadeDeletingIt()
    {
        int aId, bId, eId, firstMergeId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IMergeService>();
            var mt  = db.MediaTypes.First();

            MediaItem MakeItem(string name) => db.MediaItems.Add(new MediaItem
            {
                MediaTypeId    = mt.Id,
                Name           = name,
                NormalizedName = name.ToLowerInvariant(),
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            }).Entity;

            var a = MakeItem("Chain Merge A");
            var b = MakeItem("Chain Merge B");
            var e = MakeItem("Chain Merge E");
            db.SaveChanges();
            aId = a.Id; bId = b.Id; eId = e.Id;

            // Night 1: B merges into A.
            await svc.MergeAsync(aId, bId, null);
            firstMergeId = db.MediaItemMerges.First(m => m.LoserOriginalId == bId).Id;

            // Night 5: A (a past winner) is itself absorbed into E.
            await svc.MergeAsync(eId, aId, null);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            // The original B-into-A merge log must survive the cascade — repointed to the final
            // winner E — so B is still recoverable via Unmerge.
            var firstMerge = db.MediaItemMerges.FirstOrDefault(m => m.Id == firstMergeId);
            firstMerge.Should().NotBeNull();
            firstMerge!.WinnerId.Should().Be(eId);
            firstMerge.LoserOriginalId.Should().Be(bId);
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
