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

        var updated = await _db.MediaEnrichments.FindAsync(status.Id);
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
        mockProvider.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.musicbrainz"))
            .Returns(mockProvider.Object);

        await _svc.EnrichPendingAsync("chronicle.plugin.musicbrainz");

        var updated = await _db.MediaEnrichments.FindAsync(status.Id);
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
        mockProvider.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Still broken"));
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.musicbrainz"))
            .Returns(mockProvider.Object);

        await _svc.EnrichPendingAsync("chronicle.plugin.musicbrainz");

        var updated = await _db.MediaEnrichments.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Exhausted);
        updated.RetryCount.Should().Be(3);
    }

    [Fact]
    public async Task ResetAsync_Single_ResetsToPending()
    {
        var (item, status) = await SeedItemWithStatus(null, EnrichmentStatus.Exhausted, retryCount: 3);

        await _svc.ResetAsync("chronicle.plugin.musicbrainz", ResetScope.Single, item.Id);

        var updated = await _db.MediaEnrichments.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Pending);
        updated.RetryCount.Should().Be(0);
        updated.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task SkipAsync_SetsSkippedStatus()
    {
        var (item, status) = await SeedItemWithStatus(null, EnrichmentStatus.Pending);

        await _svc.SkipAsync(item.Id, "chronicle.plugin.musicbrainz");

        var updated = await _db.MediaEnrichments.FindAsync(status.Id);
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

        var mockProvider = new Mock<IMetadataProvider>();
        _registry.Setup(r => r.GetMetadataProviderEntries())
            .Returns(new List<(string, IMetadataProvider)>
            {
                ("chronicle.plugin.musicbrainz", mockProvider.Object)
            });

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

        var updated = await _db.MediaEnrichments.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.NotFound);
    }

    [Fact]
    public async Task ResetAsync_AllExhausted_ResetsOnlyExhaustedRows()
    {
        var (_, exhaustedStatus) = await SeedItemWithStatus(null, EnrichmentStatus.Exhausted, retryCount: 3);
        var (_, skippedStatus)   = await SeedItemWithStatus(null, EnrichmentStatus.Skipped);

        await _svc.ResetAsync("chronicle.plugin.musicbrainz", ResetScope.AllExhausted);

        var exhausted = await _db.MediaEnrichments.FindAsync(exhaustedStatus.Id);
        var skipped   = await _db.MediaEnrichments.FindAsync(skippedStatus.Id);
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

        var failed  = await _db.MediaEnrichments.FindAsync(failedStatus.Id);
        var skipped = await _db.MediaEnrichments.FindAsync(skippedStatus.Id);
        failed!.Status.Should().Be(EnrichmentStatus.Pending);
        skipped!.Status.Should().Be(EnrichmentStatus.Skipped); // unchanged
    }

    // ── GetItemsAsync tests ───────────────────────────────────────────────────

    [Fact]
    public async Task GetItemsAsync_FiltersByPluginId()
    {
        // Arrange: two enrichment rows for different plugins
        await SeedItemWithStatus(null, EnrichmentStatus.Pending, pluginId: "chronicle.plugin.musicbrainz");
        await SeedItemWithStatus(null, EnrichmentStatus.Pending, pluginId: "chronicle.plugin.tmdb");

        // Act: fetch items for only one plugin
        var result = await _svc.GetItemsAsync("chronicle.plugin.musicbrainz", null, 1, 50, null, CancellationToken.None);

        // Assert: only the MusicBrainz item is returned
        result.Total.Should().Be(1);
        result.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetItemsAsync_FiltersByStatus()
    {
        // Arrange: one Pending and one NotFound row for the same plugin
        await SeedItemWithStatus(null, EnrichmentStatus.Pending, pluginId: "chronicle.plugin.musicbrainz");
        await SeedItemWithStatus(null, EnrichmentStatus.NotFound, pluginId: "chronicle.plugin.musicbrainz");

        // Act: filter by NotFound status
        var result = await _svc.GetItemsAsync("chronicle.plugin.musicbrainz", "NotFound", 1, 50, null, CancellationToken.None);

        // Assert: only the NotFound row is returned
        result.Total.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].Status.Should().Be(EnrichmentStatus.NotFound);
    }

    [Fact]
    public async Task GetItemsAsync_PaginatesCorrectly()
    {
        // Arrange: five items for one plugin
        for (var i = 0; i < 5; i++)
            await SeedItemWithStatus(null, EnrichmentStatus.Pending, pluginId: "chronicle.plugin.musicbrainz");

        // Act: request page 1 with pageSize 2
        var result = await _svc.GetItemsAsync("chronicle.plugin.musicbrainz", null, 1, 2, null, CancellationToken.None);

        // Assert: two items on this page, total is five
        result.Items.Should().HaveCount(2);
        result.Total.Should().Be(5);
        result.PageSize.Should().Be(2);
        result.Page.Should().Be(1);
    }

    [Fact]
    public async Task GetItemsAsync_SearchFiltersByName()
    {
        // Arrange: two items with distinct names
        await SeedItemWithStatus(null, EnrichmentStatus.Pending, pluginId: "chronicle.plugin.musicbrainz", name: "Metallica");
        await SeedItemWithStatus(null, EnrichmentStatus.Pending, pluginId: "chronicle.plugin.musicbrainz", name: "Alanis Morissette");

        // Act: search for "Metallica"
        var result = await _svc.GetItemsAsync("chronicle.plugin.musicbrainz", null, 1, 50, "Metallica", CancellationToken.None);

        // Assert: only the Metallica item is returned
        result.Total.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].Name.Should().Be("Metallica");
    }

    // ── FilenameStem population tests ────────────────────────────────────────

    [Fact]
    public async Task EnrichPendingAsync_PopulatesFilenameStem_FromFileScannerFilePaths()
    {
        // Arrange: item whose tag says "(LP version)" but filename stem is the clean title.
        var item = new MediaItem
        {
            Name          = "Duck and Run (LP version)",
            MediaTypeId   = (await EnsureMediaTypeAsync("music")).Id,
            HierarchyLevel = 2,
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
            MetadataJson  = """{"fileScanner":{"filePaths":["H:/Music/3 Doors Down/Away From the Sun/01 - Duck and Run.mp3"]}}""",
        };
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();

        var row = new MediaItemEnrichment
        {
            MediaItemId = item.Id,
            PluginId    = "chronicle.plugin.musicbrainz",
            Status      = EnrichmentStatus.Pending,
            MaxRetries  = 3,
        };
        _db.MediaEnrichments.Add(row);
        await _db.SaveChangesAsync();

        MediaSearchContext? capturedCtx = null;
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.PluginId).Returns("chronicle.plugin.musicbrainz");
        provider.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "music" }]);
        provider.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
            .Callback((MediaSearchContext ctx, CancellationToken _) => capturedCtx = ctx)
            .ReturnsAsync(new List<ScoredCandidate>());
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.musicbrainz"))
            .Returns(provider.Object);

        // Act
        await _svc.EnrichPendingAsync("chronicle.plugin.musicbrainz");

        // Assert: FilenameStem must be the clean title stripped of the track-number prefix
        capturedCtx.Should().NotBeNull();
        capturedCtx!.FilenameStem.Should().Be("Duck and Run");
    }

    [Fact]
    public async Task EnrichPendingAsync_FilenameStemNull_WhenStemMatchesItemName()
    {
        // Arrange: filename stem equals the item name — no meaningful difference, so null expected.
        var item = new MediaItem
        {
            Name          = "Kryptonite",
            MediaTypeId   = (await EnsureMediaTypeAsync("music")).Id,
            HierarchyLevel = 2,
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
            MetadataJson  = """{"fileScanner":{"filePaths":["H:/Music/3 Doors Down/The Better Life/02 - Kryptonite.mp3"]}}""",
        };
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();

        var row = new MediaItemEnrichment
        {
            MediaItemId = item.Id,
            PluginId    = "chronicle.plugin.musicbrainz",
            Status      = EnrichmentStatus.Pending,
            MaxRetries  = 3,
        };
        _db.MediaEnrichments.Add(row);
        await _db.SaveChangesAsync();

        MediaSearchContext? capturedCtx = null;
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.PluginId).Returns("chronicle.plugin.musicbrainz");
        provider.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "music" }]);
        provider.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
            .Callback((MediaSearchContext ctx, CancellationToken _) => capturedCtx = ctx)
            .ReturnsAsync(new List<ScoredCandidate>());
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.musicbrainz"))
            .Returns(provider.Object);

        await _svc.EnrichPendingAsync("chronicle.plugin.musicbrainz");

        capturedCtx.Should().NotBeNull();
        capturedCtx!.FilenameStem.Should().BeNull("stem equals item name — no useful fallback");
    }

    [Fact]
    public async Task EnrichPendingAsync_FilenameStemNull_WhenNoFileScannerMetadata()
    {
        // Arrange: item imported from TMDB (no fileScanner metadata).
        var item = new MediaItem
        {
            Name          = "Blade Runner",
            MediaTypeId   = (await EnsureMediaTypeAsync("movies")).Id,
            HierarchyLevel = 0,
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
            MetadataJson  = null,
        };
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();

        var row = new MediaItemEnrichment
        {
            MediaItemId = item.Id,
            PluginId    = "chronicle.plugin.tmdb",
            Status      = EnrichmentStatus.Pending,
            MaxRetries  = 3,
        };
        _db.MediaEnrichments.Add(row);
        await _db.SaveChangesAsync();

        MediaSearchContext? capturedCtx = null;
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.PluginId).Returns("chronicle.plugin.tmdb");
        provider.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "movies" }]);
        provider.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
            .Callback((MediaSearchContext ctx, CancellationToken _) => capturedCtx = ctx)
            .ReturnsAsync(new List<ScoredCandidate>());
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.tmdb"))
            .Returns(provider.Object);

        await _svc.EnrichPendingAsync("chronicle.plugin.tmdb");

        capturedCtx.Should().NotBeNull();
        capturedCtx!.FilenameStem.Should().BeNull("no fileScanner metadata present");
    }

    private async Task<MediaType> EnsureMediaTypeAsync(string name)
    {
        var existing = await _db.MediaTypes.FirstOrDefaultAsync(m => m.Name == name);
        if (existing is not null) return existing;
        var mt = new MediaType
        {
            Name = name, DisplayName = name, HierarchyLevels = 3,
            HierarchyLabels = "Artist,Album,Track",
            InteractionVerb = "listened", ProgressUnit = "tracks"
        };
        _db.MediaTypes.Add(mt);
        await _db.SaveChangesAsync();
        return mt;
    }

    // ── ValidateYear tests ────────────────────────────────────────────────────

    [Theory]
    [InlineData(1899, null)]       // too old
    [InlineData(1900, 1900)]       // boundary: valid
    [InlineData(2024, 2024)]       // typical
    [InlineData(null, null)]       // no year stays null
    public void ValidateYear_ReturnsExpected(int? input, int? expected)
    {
        var result = MetadataEnrichmentService.ValidateYear(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void ValidateYear_FuturePlusThree_IsValid()
    {
        var farFuture = DateTime.UtcNow.Year + 3;
        MetadataEnrichmentService.ValidateYear(farFuture).Should().Be(farFuture);
    }

    [Fact]
    public void ValidateYear_FuturePlusFour_IsNull()
    {
        var tooFar = DateTime.UtcNow.Year + 4;
        MetadataEnrichmentService.ValidateYear(tooFar).Should().BeNull();
    }

    // ── BuildAltTitles tests ──────────────────────────────────────────────────

    [Fact]
    public void BuildAltTitles_YearPrefix_IsStripped()
    {
        var result = MetadataEnrichmentService.BuildAltTitles(
            name: "(2014) Remixed", filenameStem: null, preciseName: null);
        result.Should().Contain("Remixed");
        result.Should().NotContain("(2014)");
        result.Should().NotContain("2014");
    }

    [Fact]
    public void BuildAltTitles_YearSuffix_IsStripped()
    {
        var result = MetadataEnrichmentService.BuildAltTitles(
            name: "The Better Life (2000)", filenameStem: null, preciseName: null);
        result[0].Should().Be("The Better Life");
    }

    [Fact]
    public void BuildAltTitles_VersionQualifier_AddsStrippedVariant()
    {
        var result = MetadataEnrichmentService.BuildAltTitles(
            name: "Kryptonite (LP version)", filenameStem: "Kryptonite", preciseName: null);
        result.Should().Contain("Kryptonite (LP version)");
        result.Should().Contain("Kryptonite");
    }

    [Fact]
    public void BuildAltTitles_Deduplicates()
    {
        // filenameStem same as the canonical name — should not appear twice
        var result = MetadataEnrichmentService.BuildAltTitles(
            name: "Kryptonite", filenameStem: "Kryptonite", preciseName: null);
        result.Should().HaveCount(1);
    }

    [Fact]
    public void BuildAltTitles_PreciseName_PrependedFirst()
    {
        var result = MetadataEnrichmentService.BuildAltTitles(
            name: "what if", filenameStem: null, preciseName: "What If...?");
        result[0].Should().Be("What If...?");
    }

    // ── EnrichItemAsync (unified) tests ───────────────────────────────────────

    [Fact]
    public async Task EnrichItemAsync_UsesIdOverrideDirectly()
    {
        var item = await SeedRootItem("Wrong Movie", 2000);
        await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb", null, EnrichmentStatus.Pending);

        var provider = SetupProvider("chronicle.plugin.tmdb", "movies");
        provider.Setup(p => p.GetByIdAsync("movie:999", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Blade Runner", ExternalId = "movie:999" });

        var opts = new EnrichmentOptions(EnrichmentMode.Force, IdOverride: "movie:999", Cascade: false);
        await _svc.EnrichItemAsync(item.Id, "chronicle.plugin.tmdb", opts);

        var row = await _db.MediaEnrichments
            .FirstAsync(e => e.MediaItemId == item.Id && e.PluginId == "chronicle.plugin.tmdb");
        row.ExternalId.Should().Be("movie:999");
        row.Status.Should().Be(EnrichmentStatus.Completed);
    }

    [Fact]
    public async Task EnrichItemAsync_FillGaps_SkipsCompleted()
    {
        var item = await SeedRootItem("Blade Runner", 1982);
        await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb", "movie:78", EnrichmentStatus.Completed);

        var provider = SetupProvider("chronicle.plugin.tmdb", "movies");

        var opts = new EnrichmentOptions(EnrichmentMode.FillGaps, Cascade: false);
        await _svc.EnrichItemAsync(item.Id, "chronicle.plugin.tmdb", opts);

        provider.Verify(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnrichItemAsync_Force_UsesStoredExternalId()
    {
        var item = await SeedRootItem("Blade Runner", 1982);
        await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb", "movie:78", EnrichmentStatus.Completed);

        var provider = SetupProvider("chronicle.plugin.tmdb", "movies");
        provider.Setup(p => p.GetByIdAsync("movie:78", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Blade Runner", ExternalId = "movie:78" });

        var opts = new EnrichmentOptions(EnrichmentMode.Force, Cascade: false);
        await _svc.EnrichItemAsync(item.Id, "chronicle.plugin.tmdb", opts);

        provider.Verify(p => p.GetByIdAsync("movie:78", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnrichItemAsync_SearchesWhenNoStoredId()
    {
        var item = await SeedRootItem("Blade Runner", 1982);
        await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb", null, EnrichmentStatus.Pending);

        var provider = SetupProvider("chronicle.plugin.tmdb", "movies");
        provider.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScoredCandidate>
            {
                new(new MediaMetadata { Title = "Blade Runner", ExternalId = "movie:78" }, Score: 80)
            });
        provider.Setup(p => p.GetByIdAsync("movie:78", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Blade Runner", ExternalId = "movie:78" });

        var opts = new EnrichmentOptions(EnrichmentMode.FillGaps, Cascade: false);
        await _svc.EnrichItemAsync(item.Id, "chronicle.plugin.tmdb", opts);

        var row = await _db.MediaEnrichments.FirstAsync(e => e.MediaItemId == item.Id);
        row.ExternalId.Should().Be("movie:78");
        row.Status.Should().Be(EnrichmentStatus.Completed);
    }

    [Fact]
    public async Task EnrichItemAsync_SearchBelowThreshold_SetsNotFound()
    {
        var item = await SeedRootItem("Xyzzy Unmatched", null);
        await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb", null, EnrichmentStatus.Pending);

        var provider = SetupProvider("chronicle.plugin.tmdb", "movies");
        provider.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScoredCandidate>
            {
                new(new MediaMetadata { Title = "Something Else", ExternalId = "movie:1" }, Score: 20)
            });

        var opts = new EnrichmentOptions(EnrichmentMode.FillGaps, Cascade: false);
        await _svc.EnrichItemAsync(item.Id, "chronicle.plugin.tmdb", opts);

        var row = await _db.MediaEnrichments.FirstAsync(e => e.MediaItemId == item.Id);
        row.Status.Should().Be(EnrichmentStatus.NotFound);
        row.ExternalId.Should().BeNull();
    }

    // ── New helpers ───────────────────────────────────────────────────────────

    private async Task<MediaItem> SeedRootItem(string name, int? year)
    {
        if (_mediaType == null!)
        {
            _mediaType = new MediaType
            {
                Name = "movies", DisplayName = "Movies", HierarchyLevels = 1,
                HierarchyLabels = "Movie", InteractionVerb = "watched", ProgressUnit = "movies"
            };
            _db.MediaTypes.Add(_mediaType);
            await _db.SaveChangesAsync();
        }

        var item = new MediaItem
        {
            Name = name, Year = year, MediaTypeId = _mediaType.Id,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();
        return item;
    }

    private async Task<MediaItemEnrichment> SeedEnrichmentRow(
        int itemId, string pluginId, string? externalId, EnrichmentStatus status)
    {
        var row = new MediaItemEnrichment
        {
            MediaItemId = itemId, PluginId = pluginId,
            ExternalId = externalId, Status = status, MaxRetries = 3
        };
        _db.MediaEnrichments.Add(row);
        await _db.SaveChangesAsync();
        return row;
    }

    private Mock<IMetadataProvider> SetupProvider(string pluginId, string mediaTypeName)
    {
        var mock = new Mock<IMetadataProvider>();
        mock.Setup(p => p.PluginId).Returns(pluginId);
        mock.Setup(p => p.GetSupportedMediaTypes())
            .Returns(new[] { new MediaTypeSupport { MediaTypeName = mediaTypeName } });
        _registry.Setup(r => r.GetMetadataProvider(pluginId)).Returns(mock.Object);
        _registry.Setup(r => r.GetMetadataProviderEntries())
            .Returns(new[] { (pluginId, mock.Object) });
        return mock;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(MediaItem, MediaItemEnrichment)> SeedItemWithStatus(
        string? externalId, EnrichmentStatus status,
        int retryCount = 0, int maxRetries = 3,
        string pluginId = "chronicle.plugin.musicbrainz",
        string? name = null)
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
            Name = name ?? ("Item " + Guid.NewGuid().ToString("N")[..6]),
            MediaTypeId = _mediaType.Id,
            HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();

        var row = new MediaItemEnrichment
        {
            MediaItemId = item.Id,
            PluginId    = pluginId,
            ExternalId  = externalId,
            Status      = status,
            RetryCount  = retryCount,
            MaxRetries  = maxRetries
        };
        _db.MediaEnrichments.Add(row);
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
