using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// The sweep that runs every series Chronicle has matched to a Hardcover id through
/// <see cref="IBookSeriesService.EnsureSeriesStubsAsync"/> -- chained after Hardcover's own
/// fetch-missing-metadata task (see TaskSchedulerService's own call site).
/// </summary>
public class HardcoverSeriesReconcileServiceTests
{
    private static (ChronicleDbContext db, HardcoverSeriesReconcileService svc, Mock<IMetadataProvider> provider)
        Setup(bool pluginInstalled = true)
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();

        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSupportedMediaTypes()).Returns([new MediaTypeSupport { MediaTypeName = "audiobooks" }]);

        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProvider("hardcover")).Returns(pluginInstalled ? provider.Object : null);
        registry.Setup(r => r.GetMetadataProviderEntries())
            .Returns([("hardcover", provider.Object, (string?)null)]);

        var services = new ServiceCollection().AddSingleton(db)
            .AddScoped<IPluginRegistry>(_ => registry.Object)
            .AddScoped<IBookSeriesService, BookSeriesService>()
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<BookSeriesService>>(NullLogger<BookSeriesService>.Instance)
            .BuildServiceProvider();
        return (db, new HardcoverSeriesReconcileService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<HardcoverSeriesReconcileService>.Instance), provider);
    }

    private static MediaItem Item(string name, int level, int? parentId, int? number = null) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = level, ParentId = parentId, Number = number,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task SyncsEverySeriesWithAHardcoverId_AcrossMultipleAuthors()
    {
        var (db, svc, provider) = Setup();
        await using var _ = db;
        var author1 = Item("Author One", 0, null);
        var author2 = Item("Author Two", 0, null);
        db.MediaItems.AddRange(author1, author2);
        await db.SaveChangesAsync();
        var series1 = Item("Series One", 1, author1.Id);
        var series2 = Item("Series Two", 1, author2.Id);
        var notASeries = Item("Standalone, No Hardcover Id", 1, author1.Id);
        db.MediaItems.AddRange(series1, series2, notASeries);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = series1.Id, Source = "hardcover", ExternalId = "hardcover:series:1" },
            new MediaExternalId { MediaItemId = series2.Id, Source = "hardcover", ExternalId = "hardcover:series:2" });
        await db.SaveChangesAsync();

        provider.Setup(p => p.GetByIdAsync("hardcover:series:1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Series One", Results = [new MediaMetadata { ExternalId = "hardcover:1", Title = "Book A" }] });
        provider.Setup(p => p.GetByIdAsync("hardcover:series:2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Series Two", Results = [new MediaMetadata { ExternalId = "hardcover:2", Title = "Book B" }] });

        await svc.ExecuteAsync(default);

        Assert.Equal(1, await db.MediaItems.CountAsync(m => m.ParentId == series1.Id));
        Assert.Equal(1, await db.MediaItems.CountAsync(m => m.ParentId == series2.Id));
        Assert.Equal(0, await db.MediaItems.CountAsync(m => m.ParentId == notASeries.Id)); // never touched -- has no Hardcover id
    }

    [Fact]
    public async Task WithHardcoverNotInstalled_DoesNothing_AndDoesNotThrow()
    {
        var (db, svc, _) = Setup(pluginInstalled: false);
        await using var _ = db;

        await svc.ExecuteAsync(default); // must not throw
    }

    [Fact]
    public async Task AFailureOnOneSeries_DoesNotStopTheOthersFromSyncing()
    {
        var (db, svc, provider) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var broken = Item("Broken Series", 1, author.Id);
        var fine = Item("Fine Series", 1, author.Id);
        db.MediaItems.AddRange(broken, fine);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = broken.Id, Source = "hardcover", ExternalId = "hardcover:series:1" },
            new MediaExternalId { MediaItemId = fine.Id, Source = "hardcover", ExternalId = "hardcover:series:2" });
        await db.SaveChangesAsync();

        provider.Setup(p => p.GetByIdAsync("hardcover:series:1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network blip"));
        provider.Setup(p => p.GetByIdAsync("hardcover:series:2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Fine Series", Results = [new MediaMetadata { ExternalId = "hardcover:2", Title = "Book" }] });

        await svc.ExecuteAsync(default); // must not throw, and must still process "fine"

        Assert.Equal(1, await db.MediaItems.CountAsync(m => m.ParentId == fine.Id));
    }
}
