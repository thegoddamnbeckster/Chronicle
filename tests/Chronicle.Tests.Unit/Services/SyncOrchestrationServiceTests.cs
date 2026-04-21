using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class SyncOrchestrationServiceMatchTests : IDisposable
{
    private readonly ChronicleDbContext _db;
    private readonly Mock<IPluginRegistry> _registry;
    private MediaType _mediaType = null!;

    public SyncOrchestrationServiceMatchTests()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new ChronicleDbContext(opts);
        _registry = new Mock<IPluginRegistry>();
    }

    private SyncOrchestrationService BuildService(IImportProvider? provider = null)
    {
        if (provider is not null)
            _registry.Setup(r => r.GetImportProvider("chronicle.plugin.trakt")).Returns(provider);
        _registry.Setup(r => r.GetMetadataProviderEntries())
            .Returns(Array.Empty<(string, IMetadataProvider, string?)>());
        var scopeFactory = BuildScopeFactory(_db, _registry.Object);
        return new SyncOrchestrationService(scopeFactory, _registry.Object,
            Mock.Of<ILogger<SyncOrchestrationService>>());
    }

    private async Task<MediaType> EnsureMediaTypeAsync(string name = "movies")
    {
        if (_mediaType is not null) return _mediaType;
        _mediaType = new MediaType
        {
            Name = name, DisplayName = "Movies", HierarchyLevels = 1,
            HierarchyLabels = "Movie", InteractionVerb = "watched", ProgressUnit = "movies"
        };
        _db.MediaTypes.Add(_mediaType);
        await _db.SaveChangesAsync();
        return _mediaType;
    }

    [Fact]
    public async Task MatchOrCreate_FindsByExternalId()
    {
        await EnsureMediaTypeAsync();
        var item = new MediaItem { Name = "Fight Club", Year = 1999, MediaTypeId = _mediaType.Id,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.MediaItems.Add(item);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = "trakt", ExternalId = "trakt:12345" });
        await _db.SaveChangesAsync();

        var service = BuildService();
        var evt = new ImportedWatchEvent(
            "trakt:12345",
            new Dictionary<string, string>(),
            "movie", "Fight Club", 1999,
            DateTimeOffset.UtcNow, 100);

        var (matched, isNew) = await service.MatchOrCreateAsync(_db, evt, "chronicle.plugin.trakt", CancellationToken.None);

        matched.Id.Should().Be(item.Id);
        isNew.Should().BeFalse();
    }

    [Fact]
    public async Task MatchOrCreate_FindsByAdditionalId()
    {
        await EnsureMediaTypeAsync();
        var item = new MediaItem { Name = "Fight Club", Year = 1999, MediaTypeId = _mediaType.Id,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.MediaItems.Add(item);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = "tmdb", ExternalId = "movie:550" });
        await _db.SaveChangesAsync();

        var service = BuildService();
        var evt = new ImportedWatchEvent(
            "trakt:12345",
            new Dictionary<string, string> { ["tmdb"] = "movie:550" },
            "movie", "Fight Club", 1999,
            DateTimeOffset.UtcNow, 100);

        var (matched, isNew) = await service.MatchOrCreateAsync(_db, evt, "chronicle.plugin.trakt", CancellationToken.None);

        matched.Id.Should().Be(item.Id);
        isNew.Should().BeFalse();
    }

    [Fact]
    public async Task MatchOrCreate_CreateStub_WhenNoMatch()
    {
        await EnsureMediaTypeAsync();

        var mockProvider = new Mock<IImportProvider>();
        mockProvider
            .Setup(p => p.GetItemMetadataAsync("trakt:99999", "movie", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImportedItemMetadata(
                "Unknown Movie", 2020, "An overview.", null, 90,
                new Dictionary<string, string> { ["tmdb"] = "movie:88888" }));

        var service = BuildService(mockProvider.Object);
        var evt = new ImportedWatchEvent(
            "trakt:99999",
            new Dictionary<string, string> { ["tmdb"] = "movie:88888" },
            "movie", "Unknown Movie", 2020,
            DateTimeOffset.UtcNow, 100);

        var (created, isNew) = await service.MatchOrCreateAsync(_db, evt, "chronicle.plugin.trakt", CancellationToken.None);

        isNew.Should().BeTrue();
        created.Name.Should().Be("Unknown Movie");
        (await _db.MediaExternalIds.AnyAsync(e => e.Source == "trakt" && e.ExternalId == "trakt:99999"))
            .Should().BeTrue();
        (await _db.MediaExternalIds.AnyAsync(e => e.Source == "tmdb" && e.ExternalId == "movie:88888"))
            .Should().BeTrue();
    }

    private static IServiceScopeFactory BuildScopeFactory(ChronicleDbContext db, IPluginRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(registry);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    public void Dispose() => _db.Dispose();
}
