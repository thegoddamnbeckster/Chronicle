using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Confirmed live (2026-09-25): 93 movies (e.g. the 1974 "The Longest Yard") carried another film's
/// whole id set, because a merge grafted every id of the loser onto the winner. A non-people item has
/// exactly one identity per source, so a loser id that differs from the winner's own id for that
/// source names a different item and must not be grafted.
/// </summary>
public class MergeServiceForeignIdTests
{
    private static (ChronicleDbContext db, MergeService svc) Setup()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var svc = new MergeService(
            db, Mock.Of<IMetadataResolutionService>(), Mock.Of<IMovieCollectionService>(),
            Mock.Of<IFileScanService>(), NullLogger<MergeService>.Instance);
        return (db, svc);
    }

    private static async Task<(MediaItem winner, MediaItem loser)> SeedAsync(ChronicleDbContext db, string typeName)
    {
        db.MediaTypes.Add(new MediaType { Id = 1, Name = typeName, DisplayName = typeName, HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        var winner = new MediaItem { MediaTypeId = 1, Name = "Same Name", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var loser = new MediaItem { MediaTypeId = 1, Name = "Same Name", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.AddRange(winner, loser);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = winner.Id, Source = "tmdb", ExternalId = "movie:4985" },
            new MediaExternalId { MediaItemId = loser.Id, Source = "tmdb", ExternalId = "movie:9291" },
            new MediaExternalId { MediaItemId = loser.Id, Source = "imdb", ExternalId = "tt0398165" });
        await db.SaveChangesAsync();
        return (winner, loser);
    }

    [Fact]
    public async Task Movie_LoserIdDifferentFromWinnersOwnIdForThatSource_IsNotGrafted_ButAnUnownedSourceIs()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var (winner, loser) = await SeedAsync(db, "movies");

        await svc.MergeLoadedItemsAsync(db, winner, loser, null);
        await db.SaveChangesAsync(); // MergeLoadedItemsAsync leaves the commit to its caller

        var ids = await db.MediaExternalIds.Where(e => e.MediaItemId == winner.Id).ToListAsync();
        Assert.Equal(["movie:4985"], ids.Where(e => e.Source == "tmdb").Select(e => e.ExternalId));
        // The winner had no imdb id, so the loser's is a genuine addition, not a conflict.
        Assert.Contains(ids, e => e.Source == "imdb" && e.ExternalId == "tt0398165");
    }

    [Fact]
    public async Task People_KeepBothIdsOfOneSource_TwoRecordsForOneRealPersonAreLegitimate()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var (winner, loser) = await SeedAsync(db, "people");

        await svc.MergeLoadedItemsAsync(db, winner, loser, null);
        await db.SaveChangesAsync(); // MergeLoadedItemsAsync leaves the commit to its caller

        var tmdb = await db.MediaExternalIds.Where(e => e.MediaItemId == winner.Id && e.Source == "tmdb").ToListAsync();
        Assert.Equal(2, tmdb.Count);
    }
}
