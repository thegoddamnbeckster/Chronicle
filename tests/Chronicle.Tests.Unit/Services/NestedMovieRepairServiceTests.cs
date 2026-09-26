using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Live (2026-09-26): "The Animatrix" was nested under "The Matrix", so the Matrix looked like a collection
/// container and was excluded from every match -- each Kodi search minted a duplicate of it.
/// </summary>
public class NestedMovieRepairServiceTests
{
    private static async Task<(ChronicleDbContext db, NestedMovieRepairService svc)> SetupAsync()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.AddRange(
            new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow },
            new MediaType { Id = 2, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        return (db, new NestedMovieRepairService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<NestedMovieRepairService>.Instance));
    }

    private static MediaItem Item(int typeId, string name, int level, int? parentId = null) => new()
    {
        MediaTypeId = typeId, Name = name, HierarchyLevel = level, ParentId = parentId,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task AMovieNestedUnderAnotherMovie_MovesUpIntoThatMoviesCollection()
    {
        var (db, svc) = await SetupAsync();
        await using var _ = db;
        var collection = Item(1, "The Matrix Collection", 0);
        db.MediaItems.Add(collection);
        await db.SaveChangesAsync();
        var matrix = Item(1, "The Matrix", 1, collection.Id);
        db.MediaItems.Add(matrix);
        await db.SaveChangesAsync();
        var animatrix = Item(1, "The Animatrix", 1, matrix.Id);
        db.MediaItems.Add(animatrix);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Equal(collection.Id, (await db.MediaItems.FindAsync(animatrix.Id))!.ParentId);
        Assert.Equal(collection.Id, (await db.MediaItems.FindAsync(matrix.Id))!.ParentId);
        Assert.False(await db.MediaItems.AnyAsync(m => m.ParentId == matrix.Id)); // no longer looks like a container
    }

    [Fact]
    public async Task ACollectionContainerItself_AndAShowsSeasonsAndEpisodes_AreNeverTouched()
    {
        var (db, svc) = await SetupAsync();
        await using var _ = db;
        var collection = Item(1, "Some Collection", 0);
        var show = Item(2, "Show", 0);
        db.MediaItems.AddRange(collection, show);
        await db.SaveChangesAsync();
        var member = Item(1, "Member", 1, collection.Id);
        var season = Item(2, "Season 1", 1, show.Id);
        db.MediaItems.AddRange(member, season);
        await db.SaveChangesAsync();
        var episode = Item(2, "S01E01", 2, season.Id);
        db.MediaItems.Add(episode);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Equal(collection.Id, (await db.MediaItems.FindAsync(member.Id))!.ParentId);
        Assert.Equal(season.Id, (await db.MediaItems.FindAsync(episode.Id))!.ParentId);
    }
}
