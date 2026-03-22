using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class MetadataEnrichmentServiceTests : IDisposable
{
    private readonly ChronicleDbContext _db;
    private readonly Mock<IPluginRegistry> _registry;
    private readonly MetadataEnrichmentService _svc;
    private User _user = null!;
    private MediaType _mediaType = null!;

    public MetadataEnrichmentServiceTests()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new ChronicleDbContext(opts);
        _registry = new Mock<IPluginRegistry>();
        var scopeFactory = BuildScopeFactory(_db, _registry.Object);
        _svc = new MetadataEnrichmentService(scopeFactory, Mock.Of<ILogger<MetadataEnrichmentService>>());
    }

    [Fact]
    public async Task EnrichPendingAsync_CallsGetByIdWhenExternalIdKnown()
    {
        var (item, status) = await SeedItemWithStatus("artist:abc-123", EnrichmentStatus.Pending);

        var mockProvider = new Mock<IMetadataProvider>();
        mockProvider.Setup(p => p.PluginId).Returns("chronicle.plugin.musicbrainz");
        mockProvider.Setup(p => p.GetByIdAsync("artist:abc-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Radiohead", ExternalId = "artist:abc-123" });
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.musicbrainz"))
            .Returns(mockProvider.Object);

        await _svc.EnrichPendingAsync("chronicle.plugin.musicbrainz");

        var updated = await _db.EnrichmentStatuses.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Completed);
        updated.LastCompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task EnrichPendingAsync_IncrementsRetryCountOnFailure()
    {
        var (item, status) = await SeedItemWithStatus(null, EnrichmentStatus.Pending);

        var mockProvider = new Mock<IMetadataProvider>();
        mockProvider.Setup(p => p.PluginId).Returns("chronicle.plugin.musicbrainz");
        mockProvider.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "music" }]);
        mockProvider.Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.musicbrainz"))
            .Returns(mockProvider.Object);

        await _svc.EnrichPendingAsync("chronicle.plugin.musicbrainz");

        var updated = await _db.EnrichmentStatuses.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Failed);
        updated.RetryCount.Should().Be(1);
        updated.ErrorMessage.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task EnrichPendingAsync_SetsExhaustedWhenRetriesExceeded()
    {
        var (item, status) = await SeedItemWithStatus(null, EnrichmentStatus.Failed, retryCount: 2, maxRetries: 3);

        var mockProvider = new Mock<IMetadataProvider>();
        mockProvider.Setup(p => p.PluginId).Returns("chronicle.plugin.musicbrainz");
        mockProvider.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "music" }]);
        mockProvider.Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Still broken"));
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.musicbrainz"))
            .Returns(mockProvider.Object);

        await _svc.EnrichPendingAsync("chronicle.plugin.musicbrainz");

        var updated = await _db.EnrichmentStatuses.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Exhausted);
        updated.RetryCount.Should().Be(3);
    }

    [Fact]
    public async Task ResetAsync_Single_ResetsToPending()
    {
        var (item, status) = await SeedItemWithStatus(null, EnrichmentStatus.Exhausted, retryCount: 3);

        await _svc.ResetAsync("chronicle.plugin.musicbrainz", ResetScope.Single, item.Id);

        var updated = await _db.EnrichmentStatuses.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Pending);
        updated.RetryCount.Should().Be(0);
        updated.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task SkipAsync_SetsSkippedStatus()
    {
        var (item, status) = await SeedItemWithStatus(null, EnrichmentStatus.Pending);

        await _svc.SkipAsync(item.Id, "chronicle.plugin.musicbrainz");

        var updated = await _db.EnrichmentStatuses.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Skipped);
    }

    [Fact]
    public async Task GetStatsAsync_ReturnsCountsPerPlugin()
    {
        // Seed a Plugin record — GetStatsAsync now returns one row per installed plugin
        _db.Plugins.Add(new Chronicle.Core.Models.Plugin
        {
            PluginId = "chronicle.plugin.musicbrainz",
            Name = "MusicBrainz",
            Author = "Test",
            Version = "1.0.0",
            DllPath = "/fake/path.dll",
            IsEnabled = true,
            InstalledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await SeedItemWithStatus(null, EnrichmentStatus.Pending);
        await SeedItemWithStatus(null, EnrichmentStatus.Completed);

        var stats = await _svc.GetStatsAsync();

        stats.Should().HaveCount(1);
        stats[0].PluginId.Should().Be("chronicle.plugin.musicbrainz");
        stats[0].Pending.Should().Be(1);
        stats[0].Completed.Should().Be(1);
    }

    [Fact]
    public async Task EnrichPendingAsync_SetsNotFoundWhenResultHasNoExternalId()
    {
        var (item, status) = await SeedItemWithStatus("artist:abc-123", EnrichmentStatus.Pending);

        var mockProvider = new Mock<IMetadataProvider>();
        mockProvider.Setup(p => p.PluginId).Returns("chronicle.plugin.musicbrainz");
        mockProvider.Setup(p => p.GetByIdAsync("artist:abc-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Unknown", ExternalId = null });
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.musicbrainz"))
            .Returns(mockProvider.Object);

        await _svc.EnrichPendingAsync("chronicle.plugin.musicbrainz");

        var updated = await _db.EnrichmentStatuses.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.NotFound);
    }

    [Fact]
    public async Task ResetAsync_AllExhausted_ResetsOnlyExhaustedRows()
    {
        var (_, exhaustedStatus) = await SeedItemWithStatus(null, EnrichmentStatus.Exhausted, retryCount: 3);
        var (_, skippedStatus)   = await SeedItemWithStatus(null, EnrichmentStatus.Skipped);

        await _svc.ResetAsync("chronicle.plugin.musicbrainz", ResetScope.AllExhausted);

        var exhausted = await _db.EnrichmentStatuses.FindAsync(exhaustedStatus.Id);
        var skipped   = await _db.EnrichmentStatuses.FindAsync(skippedStatus.Id);
        exhausted!.Status.Should().Be(EnrichmentStatus.Pending);
        exhausted.RetryCount.Should().Be(0);
        skipped!.Status.Should().Be(EnrichmentStatus.Skipped); // unchanged
    }

    [Fact]
    public async Task ResetAsync_AllForPlugin_LeavesSkippedRowsAlone()
    {
        var (_, failedStatus)  = await SeedItemWithStatus(null, EnrichmentStatus.Failed);
        var (_, skippedStatus) = await SeedItemWithStatus(null, EnrichmentStatus.Skipped);

        await _svc.ResetAsync("chronicle.plugin.musicbrainz", ResetScope.AllForPlugin);

        var failed  = await _db.EnrichmentStatuses.FindAsync(failedStatus.Id);
        var skipped = await _db.EnrichmentStatuses.FindAsync(skippedStatus.Id);
        failed!.Status.Should().Be(EnrichmentStatus.Pending);
        skipped!.Status.Should().Be(EnrichmentStatus.Skipped); // unchanged
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(MediaItem, MediaItemEnrichmentStatus)> SeedItemWithStatus(
        string? externalId, EnrichmentStatus status,
        int retryCount = 0, int maxRetries = 3)
    {
        if (_user == null!)
        {
            _user = new User { Username = "u", PasswordHash = "h", Email = "e@e.com" };
            _db.Users.Add(_user);
            _mediaType = new MediaType
            {
                Name = "music",
                DisplayName = "Music", HierarchyLevels = 3,
                HierarchyLabels = "Artist,Album,Track",
                InteractionVerb = "listened", ProgressUnit = "tracks"
            };
            _db.MediaTypes.Add(_mediaType);
            await _db.SaveChangesAsync();
        }

        var item = new MediaItem
        {
            Name = "Item " + Guid.NewGuid().ToString("N")[..6],
            MediaTypeId = _mediaType.Id,
            HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();

        var row = new MediaItemEnrichmentStatus
        {
            MediaItemId = item.Id,
            PluginId    = "chronicle.plugin.musicbrainz",
            ExternalId  = externalId,
            Status      = status,
            RetryCount  = retryCount,
            MaxRetries  = maxRetries
        };
        _db.EnrichmentStatuses.Add(row);
        await _db.SaveChangesAsync();

        return (item, row);
    }

    private static IServiceScopeFactory BuildScopeFactory(ChronicleDbContext db, IPluginRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(registry);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    public void Dispose() => _db.Dispose();
}
