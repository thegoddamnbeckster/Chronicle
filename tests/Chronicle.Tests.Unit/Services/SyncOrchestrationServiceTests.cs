using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
            Mock.Of<IMetadataResolutionService>(),
            Mock.Of<ILogger<SyncOrchestrationService>>(),
            Mock.Of<IHostApplicationLifetime>());
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
        // Year is its own first-class MediaItem column (used throughout matching/dedup logic
        // elsewhere in the codebase) — CreateStubAsync stores it there, not embedded in Name.
        created.Name.Should().Be("Unknown Movie");
        created.Year.Should().Be(2020);
        (await _db.MediaExternalIds.AnyAsync(e => e.Source == "trakt" && e.ExternalId == "trakt:99999"))
            .Should().BeTrue();
        (await _db.MediaExternalIds.AnyAsync(e => e.Source == "tmdb" && e.ExternalId == "movie:88888"))
            .Should().BeTrue();
    }

    // ── Book hierarchy tests ──────────────────────────────────────────────────

    [Fact]
    public async Task MatchOrCreate_Book_CreatesAuthorAndBookStubs()
    {
        // Arrange — "books" media type
        _mediaType = new MediaType
        {
            Name = "books", DisplayName = "Books", HierarchyLevels = 3,
            HierarchyLabels = "Author|Series|Book", InteractionVerb = "read", ProgressUnit = "books"
        };
        _db.MediaTypes.Add(_mediaType);
        await _db.SaveChangesAsync();

        var service = BuildService();
        var evt = new ImportedWatchEvent(
            "hardcover:42",
            new Dictionary<string, string>(),
            "books", "Elantris", 2005,
            DateTimeOffset.UtcNow, 100.0,
            AuthorName: "Brandon Sanderson");

        // Act
        var (book, isNew) = await service.MatchOrCreateAsync(_db, evt, "chronicle.plugin.hardcover", CancellationToken.None);

        // Assert — book stub created
        isNew.Should().BeTrue();
        book.HierarchyLevel.Should().Be(1); // standalone: directly under author

        // Author stub must exist at level 0
        var author = await _db.MediaItems.FirstOrDefaultAsync(i => i.Name == "Brandon Sanderson" && i.HierarchyLevel == 0);
        author.Should().NotBeNull();
        book.ParentId.Should().Be(author!.Id);
    }

    [Fact]
    public async Task MatchOrCreate_Book_CreatesAuthorSeriesBookTree()
    {
        // Arrange — "books" media type
        _mediaType = new MediaType
        {
            Name = "books", DisplayName = "Books", HierarchyLevels = 3,
            HierarchyLabels = "Author|Series|Book", InteractionVerb = "read", ProgressUnit = "books"
        };
        _db.MediaTypes.Add(_mediaType);
        await _db.SaveChangesAsync();

        var service = BuildService();
        var evt = new ImportedWatchEvent(
            "hardcover:99",
            new Dictionary<string, string>(),
            "books", "The Way of Kings", 2010,
            DateTimeOffset.UtcNow, 100.0,
            AuthorName: "Brandon Sanderson",
            SeriesName: "The Stormlight Archive");

        // Act
        var (book, isNew) = await service.MatchOrCreateAsync(_db, evt, "chronicle.plugin.hardcover", CancellationToken.None);

        // Assert
        isNew.Should().BeTrue();
        book.HierarchyLevel.Should().Be(2); // under series

        var series = await _db.MediaItems.FirstOrDefaultAsync(i => i.Name == "The Stormlight Archive" && i.HierarchyLevel == 1);
        series.Should().NotBeNull();
        book.ParentId.Should().Be(series!.Id);

        var author = await _db.MediaItems.FirstOrDefaultAsync(i => i.Name == "Brandon Sanderson" && i.HierarchyLevel == 0);
        author.Should().NotBeNull();
        series.ParentId.Should().Be(author!.Id);
    }

    // ── TimestampIsApproximate propagation ──────────────────────────────────────

    [Fact]
    public async Task SyncAsync_PersistsTimestampIsApproximate_WhenEventFlaggedApproximate()
    {
        // The exact case this covers: a SIMKL show bulk-marked "completed" shares one
        // last-watched date across every episode -- ImportedWatchEvent.WatchedAtIsApproximate
        // must survive all the way into the persisted InteractionEvent so the History page
        // can tell "this episode's own genuine watch time" apart from "borrowed from the show".
        await EnsureMediaTypeAsync();
        var user = new User { Username = "approx_test", PasswordHash = "x",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var sharedShowDate = new DateTimeOffset(2026, 8, 20, 21, 4, 0, TimeSpan.Zero);
        var mockProvider = new Mock<IImportProvider>();
        mockProvider.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockProvider.Setup(p => p.GetWatchHistoryAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ImportedWatchEvent(
                "trakt:12345", new Dictionary<string, string>(), "movie", "Fight Club", 1999,
                sharedShowDate, 100, WatchedAtIsApproximate: true)]);
        mockProvider.Setup(p => p.GetRatingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockProvider.Setup(p => p.GetWatchlistAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var service = BuildService(mockProvider.Object);

        await service.SyncAsync("chronicle.plugin.trakt", fullSync: true, userId: user.Id);

        var stored = await _db.InteractionEvents.SingleAsync(e => e.UserId == user.Id);
        stored.TimestampIsApproximate.Should().BeTrue();
        stored.Timestamp.Should().Be(sharedShowDate.UtcDateTime);
    }

    [Fact]
    public async Task SyncAsync_LeavesTimestampIsApproximateFalse_ForARealPerItemTimestamp()
    {
        await EnsureMediaTypeAsync();
        var user = new User { Username = "exact_test", PasswordHash = "x",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var mockProvider = new Mock<IImportProvider>();
        mockProvider.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockProvider.Setup(p => p.GetWatchHistoryAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ImportedWatchEvent(
                "trakt:12345", new Dictionary<string, string>(), "movie", "Fight Club", 1999,
                DateTimeOffset.UtcNow, 100, WatchedAtIsApproximate: false)]);
        mockProvider.Setup(p => p.GetRatingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockProvider.Setup(p => p.GetWatchlistAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var service = BuildService(mockProvider.Object);

        await service.SyncAsync("chronicle.plugin.trakt", fullSync: true, userId: user.Id);

        var stored = await _db.InteractionEvents.SingleAsync(e => e.UserId == user.Id);
        stored.TimestampIsApproximate.Should().BeFalse();
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
