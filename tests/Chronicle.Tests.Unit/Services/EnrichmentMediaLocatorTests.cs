using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// When an application the owner runs (Lidarr for music, Readarr for books) knows exactly which item this is, enrichment must fetch
/// that id from the provider instead of searching the provider by name, and keep every id the application reported on the item. A
/// locator that knows nothing, fails, or names an id the provider cannot fetch leaves the name search exactly as it was.
/// </summary>
public sealed class EnrichmentMediaLocatorTests : IDisposable
{
    private readonly string PluginId = "chronicle.plugin.loctest" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chronicle-loc-" + Guid.NewGuid().ToString("N"));
    private readonly DbContextOptions<ChronicleDbContext> _options;
    private readonly Mock<IPluginRegistry> _registry = new();
    private readonly Mock<IMetadataProvider> _provider = new();
    private readonly FakeItemLocator _locator = new();

    private sealed class FakeItemLocator : IMediaLocator
    {
        public Func<LocatorQuery, LocatedMedia?> Answer { get; set; } = _ => null;
        public List<LocatorQuery> Asked { get; } = [];
        public IReadOnlyList<string> SupportedMediaTypes { get; set; } = ["music"];
        public string PluginId => "chronicle.plugin.fakelocator";
        public string Name => "Fake locator";
        public string Version => "1.0.0";
        public PluginSettingsSchema GetSettingsSchema() => new();
        public void Configure(IReadOnlyDictionary<string, string> settings) { }
        public bool Handles(LocatorFamily family) => false;
        public Task<LocatedMedia?> LocateAsync(LocatorFamily family, LocatorQuery query, CancellationToken ct = default) => Task.FromResult<LocatedMedia?>(null);
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<LocatedMedia?> LocateItemAsync(LocatorQuery query, CancellationToken ct = default)
        {
            Asked.Add(query);
            return Task.FromResult(Answer(query));
        }
    }

    public EnrichmentMediaLocatorTests()
    {
        Directory.CreateDirectory(_dir);
        _options = new DbContextOptionsBuilder<ChronicleDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "t.db")};Pooling=False").Options;
        using var db = new ChronicleDbContext(_options);
        db.Database.EnsureCreated();

        _provider.Setup(p => p.PluginId).Returns(PluginId);
        _provider.Setup(p => p.GetSupportedMediaTypes()).Returns([new MediaTypeSupport { MediaTypeName = "music" }]);
        _provider.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScoredCandidate>());
        _registry.Setup(r => r.GetMetadataProvider(PluginId)).Returns(_provider.Object);
        _registry.Setup(r => r.GetMediaLocators()).Returns(() => [_locator]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* scratch */ }
    }

    private MetadataEnrichmentService NewService(ChronicleDbContext shared)
    {
        var services = new ServiceCollection();
        services.AddSingleton(shared);
        services.AddSingleton(_registry.Object);
        return new MetadataEnrichmentService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IMetadataResolutionService>(), Mock.Of<IMovieCollectionService>(), Mock.Of<IMetadataUrlValidator>(),
            Mock.Of<IPersonResolutionService>(), Mock.Of<ILogger<MetadataEnrichmentService>>());
    }

    private async Task<(int Item, int Row)> SeedAsync(ChronicleDbContext db, string name, int level = 0, int? parentId = null)
    {
        var type = await db.MediaTypes.FirstAsync(t => t.Name == "music");
        var item = new MediaItem { Name = name, MediaTypeId = type.Id, HierarchyLevel = level, ParentId = parentId, Year = 2005, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        var row = new MediaItemEnrichment { MediaItemId = item.Id, PluginId = PluginId, Status = EnrichmentStatus.Pending, MaxRetries = 3 };   // no id yet: it would be searched by name
        db.MediaEnrichments.Add(row);
        await db.SaveChangesAsync();
        return (item.Id, row.Id);
    }

    private string Source => PluginIdHelper.ToSource(PluginId);

    [Fact]
    public async Task ALocatedIdForThisProvider_IsFetchedDirectly_NotSearchedByName_AndEveryIdIsKept()
    {
        await using var db = new ChronicleDbContext(_options);
        var (itemId, rowId) = await SeedAsync(db, "3 Doors Down");
        _locator.Answer = _ => new LocatedMedia("3 Doors Down", null,
            new Dictionary<string, string> { [Source] = "artist:abc", ["otherhint"] = "h-1" }, Reason: "Lidarr has the artist");
        _provider.Setup(p => p.GetByIdAsync("artist:abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "3 Doors Down", ExternalId = "artist:abc" });

        await NewService(db).EnrichPendingAsync(PluginId);

        _provider.Verify(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var check = new ChronicleDbContext(_options);
        var row = await check.MediaEnrichments.AsNoTracking().SingleAsync(e => e.Id == rowId);
        row.Status.Should().Be(EnrichmentStatus.Completed);
        row.ExternalId.Should().Be("artist:abc");
        var ids = await check.MediaExternalIds.Where(e => e.MediaItemId == itemId).Select(e => e.Source + "=" + e.ExternalId).ToListAsync();
        ids.Should().Contain("otherhint=h-1", "every id the application reports is kept as a hint other providers can read");
    }

    [Fact]
    public async Task TheLocatorIsToldTheItemsNameParentLevelAndType()
    {
        await using var db = new ChronicleDbContext(_options);
        var (artist, _) = await SeedAsync(db, "3 Doors Down");
        var (album, _) = await SeedAsync(db, "Seventeen Days", level: 1, parentId: artist);

        await NewService(db).EnrichPendingAsync(PluginId);

        _locator.Asked.Should().Contain(q => q.Title == "Seventeen Days" && q.ParentName == "3 Doors Down" && q.HierarchyLevel == 1 && q.MediaTypeName == "music");
        _locator.Asked.Should().Contain(q => q.Title == "3 Doors Down" && q.ParentName == null && q.HierarchyLevel == 0);
    }

    [Fact]
    public async Task ALocatorThatKnowsNothing_LeavesTheNameSearchAsItWas()
    {
        await using var db = new ChronicleDbContext(_options);
        await SeedAsync(db, "Unknown Artist");

        await NewService(db).EnrichPendingAsync(PluginId);

        _locator.Asked.Should().NotBeEmpty();
        _provider.Verify(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _provider.Verify(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ALocatedIdTheProviderCannotFetch_FallsBackToTheNameSearch()
    {
        await using var db = new ChronicleDbContext(_options);
        var (_, rowId) = await SeedAsync(db, "Mismatch");
        _locator.Answer = _ => new LocatedMedia("Mismatch", null, new Dictionary<string, string> { [Source] = "artist:gone" });
        _provider.Setup(p => p.GetByIdAsync("artist:gone", It.IsAny<CancellationToken>())).ReturnsAsync((MediaMetadata)null!);

        await NewService(db).EnrichPendingAsync(PluginId);

        _provider.Verify(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        await using var check = new ChronicleDbContext(_options);
        (await check.MediaEnrichments.AsNoTracking().SingleAsync(e => e.Id == rowId)).ExternalId.Should().NotBe("artist:gone");
    }

    [Fact]
    public async Task ALocatorThatThrows_IsSkipped_AndTheNameSearchStillRuns()
    {
        await using var db = new ChronicleDbContext(_options);
        await SeedAsync(db, "Throws");
        _locator.Answer = _ => throw new InvalidOperationException("Lidarr is down");

        var run = () => NewService(db).EnrichPendingAsync(PluginId);

        await run.Should().NotThrowAsync();
        _provider.Verify(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ALocatorForOtherMediaTypes_IsNotAskedAboutThisOne()
    {
        await using var db = new ChronicleDbContext(_options);
        await SeedAsync(db, "Some Artist");
        _locator.SupportedMediaTypes = ["books"];

        await NewService(db).EnrichPendingAsync(PluginId);

        _locator.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task AnIdAnotherItemAlreadyHolds_IsNotStoredTwice_AndEnrichmentStillSucceeds()
    {
        await using var db = new ChronicleDbContext(_options);
        var (owner, _) = await SeedAsync(db, "First Holder");
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = owner, Source = "otherhint", ExternalId = "shared-1" });
        await db.SaveChangesAsync();
        var (second, secondRow) = await SeedAsync(db, "Second");
        _locator.Answer = q => q.Title == "Second"
            ? new LocatedMedia("Second", null, new Dictionary<string, string> { [Source] = "artist:second", ["otherhint"] = "shared-1" })
            : null;
        _provider.Setup(p => p.GetByIdAsync("artist:second", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Second", ExternalId = "artist:second" });

        await NewService(db).EnrichPendingAsync(PluginId);

        await using var check = new ChronicleDbContext(_options);
        (await check.MediaEnrichments.AsNoTracking().SingleAsync(e => e.Id == secondRow)).Status.Should().Be(EnrichmentStatus.Completed);
        (await check.MediaExternalIds.CountAsync(e => e.Source == "otherhint" && e.ExternalId == "shared-1")).Should().Be(1);
        (await check.MediaExternalIds.AnyAsync(e => e.MediaItemId == second && e.Source == "otherhint")).Should().BeFalse();
    }
}
