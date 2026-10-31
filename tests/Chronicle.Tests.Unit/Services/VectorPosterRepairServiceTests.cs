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
/// An item whose poster is an SVG (which Kodi cannot draw) is resolved again so the next provider's image is used;
/// anything else, and people, are left alone.
/// </summary>
public class VectorPosterRepairServiceTests
{
    private const string Svg = "https://upload.wikimedia.org/wikipedia/commons/d/df/The_Voice_logo.svg?utm_source=x";
    private const string Jpg = "https://static.tvmaze.com/uploads/images/original_untouched/637/1594810.jpg";

    private static async Task<(ChronicleDbContext Db, Mock<IMetadataResolutionService> Resolver, VectorPosterRepairService Service)> BuildAsync()
    {
        RegistryFixtures.LoadBuiltIns();
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.AddRange(
            new MediaType { Id = 1, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow },
            new MediaType { Id = 2, Name = "people", IsPersonType = true, DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        db.MediaItems.AddRange(
            Item("The Voice", 1, Svg),
            Item("Has a real poster", 1, Jpg),
            Item("Margie Adam", 2, Svg));
        await db.SaveChangesAsync();

        var resolver = new Mock<IMetadataResolutionService>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<MediaItem>(), It.IsAny<ChronicleDbContext>(), It.IsAny<CancellationToken>()))
            .Callback((MediaItem i, ChronicleDbContext _, CancellationToken _) => i.PosterUrl = Jpg)
            .Returns(Task.CompletedTask);
        var provider = new ServiceCollection().AddSingleton(db).AddSingleton(resolver.Object).BuildServiceProvider();
        return (db, resolver, new VectorPosterRepairService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<VectorPosterRepairService>.Instance));
    }

    private static MediaItem Item(string name, int typeId, string poster) => new()
    {
        MediaTypeId = typeId, Name = name, HierarchyLevel = 0, PosterUrl = poster,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task An_item_with_an_svg_poster_is_resolved_again_and_picks_up_the_new_one()
    {
        var (db, resolver, service) = await BuildAsync();
        await using var _ = db;

        await service.ExecuteAsync(CancellationToken.None);

        Assert.Equal(Jpg, db.MediaItems.Single(m => m.Name == "The Voice").PosterUrl);
        resolver.Verify(r => r.ResolveAsync(It.Is<MediaItem>(m => m.Name == "The Voice"), It.IsAny<ChronicleDbContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Items_with_a_real_poster_and_people_are_not_touched()
    {
        var (db, resolver, service) = await BuildAsync();
        await using var _ = db;

        await service.ExecuteAsync(CancellationToken.None);

        resolver.Verify(r => r.ResolveAsync(It.IsAny<MediaItem>(), It.IsAny<ChronicleDbContext>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(Svg, db.MediaItems.Single(m => m.Name == "Margie Adam").PosterUrl);
    }
}
