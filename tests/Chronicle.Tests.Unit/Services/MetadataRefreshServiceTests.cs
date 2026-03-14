using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class MetadataRefreshServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChronicleDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(opts);
    }

    private static IServiceScopeFactory MakeScopeFactory(ChronicleDbContext db, IPluginRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<ChronicleDbContext>(sp => sp.GetRequiredService<ChronicleDbContext>());
        services.AddSingleton(registry);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Seeds the DB with a Movies media type and Fight Club media item.</summary>
    private static async Task<(MediaType mt, MediaItem item)> SeedMoviesAsync(ChronicleDbContext db)
    {
        var mt = new MediaType
        {
            Id = 1,
            Name = "Movies",
            DisplayName = "Movies",
            HierarchyLevels = 1,
            CreatedAt = DateTime.UtcNow
        };
        db.MediaTypes.Add(mt);
        await db.SaveChangesAsync();

        var item = new MediaItem
        {
            Id = 1,
            MediaTypeId = mt.Id,
            Name = "Fight Club",
            Year = 1999,
            HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        return (mt, item);
    }

    private static Mock<IMetadataProvider> MakeProvider(string mediaTypeName = "Movies")
    {
        var mock = new Mock<IMetadataProvider>();
        mock.Setup(p => p.PluginId).Returns("chronicle.plugin.tmdb");
        mock.Setup(p => p.Name).Returns("TMDB");
        mock.Setup(p => p.GetSupportedMediaTypes()).Returns(
        [
            new MediaTypeSupport { MediaTypeName = mediaTypeName }
        ]);
        return mock;
    }

    // ── Test 1 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshItemAsync_CallsProviderAndWritesLog()
    {
        // Arrange
        var db = MakeDb();
        var (_, _) = await SeedMoviesAsync(db);

        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = 1,
            Source = "tmdb",
            ExternalId = "movie:550"
        });
        await db.SaveChangesAsync();

        var providerMock = MakeProvider("Movies");
        providerMock
            .Setup(p => p.GetByIdAsync("movie:550", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Fight Club", ExternalId = "movie:550", Source = "tmdb" });

        var registryMock = new Mock<IPluginRegistry>();
        registryMock.Setup(r => r.GetMetadataProviders())
            .Returns([providerMock.Object]);

        var scopeFactory = MakeScopeFactory(db, registryMock.Object);

        var svc = new MetadataRefreshService(scopeFactory);

        // Act
        await svc.RefreshItemAsync(1);

        // Assert
        var logs = db.MediaItemRefreshLogs.Where(l => l.MediaItemId == 1).ToList();
        logs.Should().HaveCount(1);
        logs[0].ProviderName.Should().Be("TMDB");
        logs[0].Succeeded.Should().BeTrue();
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshItemAsync_SkipsProviderThatDoesNotSupportMediaType()
    {
        // Arrange
        var db = MakeDb();
        await SeedMoviesAsync(db);

        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = 1,
            Source = "tmdb",
            ExternalId = "movie:550"
        });
        await db.SaveChangesAsync();

        // Provider only supports "Music", not "Movies"
        var providerMock = MakeProvider("Music");

        var registryMock = new Mock<IPluginRegistry>();
        registryMock.Setup(r => r.GetMetadataProviders())
            .Returns([providerMock.Object]);

        var scopeFactory = MakeScopeFactory(db, registryMock.Object);
        var svc = new MetadataRefreshService(scopeFactory);

        // Act
        await svc.RefreshItemAsync(1);

        // Assert: no log entries written, GetByIdAsync never called
        db.MediaItemRefreshLogs.Should().BeEmpty();
        providerMock.Verify(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshItemAsync_SearchesByNameWhenNoExternalId()
    {
        // Arrange — item has NO external IDs
        var db = MakeDb();
        await SeedMoviesAsync(db);

        var providerMock = MakeProvider("Movies");

        // SearchAsync returns a result list
        providerMock
            .Setup(p => p.SearchAsync("Fight Club", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata
            {
                Results =
                [
                    new MediaMetadata { ExternalId = "movie:550", Title = "Fight Club", Year = 1999, Source = "tmdb" }
                ]
            });

        // GetByIdAsync returns full metadata for the top result
        providerMock
            .Setup(p => p.GetByIdAsync("movie:550", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Fight Club", ExternalId = "movie:550", Source = "tmdb" });

        var registryMock = new Mock<IPluginRegistry>();
        registryMock.Setup(r => r.GetMetadataProviders())
            .Returns([providerMock.Object]);

        var scopeFactory = MakeScopeFactory(db, registryMock.Object);
        var svc = new MetadataRefreshService(scopeFactory);

        // Act
        await svc.RefreshItemAsync(1);

        // Assert
        providerMock.Verify(
            p => p.SearchAsync("Fight Club", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);

        providerMock.Verify(
            p => p.GetByIdAsync("movie:550", It.IsAny<CancellationToken>()),
            Times.Once);

        db.MediaExternalIds
            .Should().Contain(e => e.MediaItemId == 1 && e.ExternalId == "movie:550");
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRefreshLogsAsync_ReturnsLatestPerProvider()
    {
        // Arrange — 3 log entries: TMDB×2, LastFM×1
        var db = MakeDb();
        await SeedMoviesAsync(db);

        var now = DateTime.UtcNow;

        db.MediaItemRefreshLogs.AddRange(
            new MediaItemRefreshLog
            {
                MediaItemId = 1,
                ProviderName = "TMDB",
                RefreshedAt = now.AddHours(-2),
                Succeeded = true
            },
            new MediaItemRefreshLog
            {
                MediaItemId = 1,
                ProviderName = "TMDB",
                RefreshedAt = now.AddHours(-1),
                Succeeded = true
            },
            new MediaItemRefreshLog
            {
                MediaItemId = 1,
                ProviderName = "LastFM",
                RefreshedAt = now.AddHours(-3),
                Succeeded = false,
                ErrorMessage = "Timeout"
            });
        await db.SaveChangesAsync();

        var registryMock = new Mock<IPluginRegistry>();
        registryMock.Setup(r => r.GetMetadataProviders()).Returns([]);

        var scopeFactory = MakeScopeFactory(db, registryMock.Object);
        var svc = new MetadataRefreshService(scopeFactory);

        // Act
        var logs = await svc.GetRefreshLogsAsync(1);

        // Assert: one entry per provider (most recent TMDB, only LastFM)
        logs.Should().HaveCount(2);

        var tmdbLog = logs.Single(l => l.ProviderName == "TMDB");
        tmdbLog.RefreshedAt.Should().BeCloseTo(now.AddHours(-1), TimeSpan.FromSeconds(5));

        logs.Should().Contain(l => l.ProviderName == "LastFM");
    }
}
