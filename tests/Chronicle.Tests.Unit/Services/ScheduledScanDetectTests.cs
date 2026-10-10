using Chronicle.Core.Models;
using Chronicle.Core.Models.Scan;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Chronicle.Tests.Unit.Services;

/// <summary>The nightly scan for a saved folder that sorts each file into its own media type, and for per-folder related files.</summary>
public class ScheduledScanDetectTests
{
    private readonly ChronicleDbContext _db = new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IFileScanService> _scan = new();
    private readonly Mock<IScanFolderService> _folders = new();
    private readonly Mock<IKodiDeviceService> _kodi = new();
    private readonly List<ImportGroupsRequest> _imports = [];

    public ScheduledScanDetectTests()
    {
        _db.MediaTypes.AddRange(
            new MediaType { Id = 1, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow },
            new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        _scan.Setup(s => s.GetConfidenceThresholdAsync("movies", It.IsAny<CancellationToken>())).ReturnsAsync(75);
        _scan.Setup(s => s.GetConfidenceThresholdAsync("tv", It.IsAny<CancellationToken>())).ReturnsAsync(90);
        _scan.Setup(s => s.GetConfidenceThresholdAsync("", It.IsAny<CancellationToken>())).ReturnsAsync(80);
        _scan.Setup(s => s.ImportGroupsAsync(It.IsAny<ImportGroupsRequest>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<ImportGroupsRequest, IReadOnlyList<int>, CancellationToken, int, bool>((r, _, _, _, _) => _imports.Add(r))
            .ReturnsAsync((ImportGroupsRequest r, IReadOnlyList<int> _, CancellationToken _, int _, bool _) =>
                new ImportApprovedSummary(r.Groups.Count, 0, [], 0));
        _kodi.Setup(k => k.SignalNewContentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private ScheduledScanService Service(ScanFolder folder)
    {
        _folders.Setup(f => f.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([folder]);
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(_scan.Object);
        services.AddSingleton(_folders.Object);
        services.AddSingleton(_kodi.Object);
        services.AddSingleton(new Mock<IMetadataEnrichmentService>().Object);
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetFileScannerPlugins()).Returns([]);
        return new ScheduledScanService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new ImportProgressService(), registry.Object);
    }

    private static ScanGroup Group(string name, int typeId, string key, double confidence) => new()
    {
        Name = name, GroupKey = name.ToLowerInvariant(), ConfidenceScore = confidence, MediaTypeId = typeId, MediaTypeName = key, MediaTypeKey = key,
        Files = [$"E:/in/{name}.mkv"],
    };

    [Fact]
    public async Task AFolderWithNoType_IsScannedByDetectingEachFile_AndImportedOnePerType()
    {
        var folder = new ScanFolder { Id = 1, Path = "E:/in", MediaTypeId = null, IsEnabled = true };
        _scan.Setup(s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanGroupResult { Groups = [Group("Heat", 2, "movies", 0.8), Group("Show", 1, "tv", 0.95)] });

        await Service(folder).ExecuteAsync(CancellationToken.None);

        _scan.Verify(s => s.PreviewGroupedAsync(It.Is<ScanPreviewRequest>(r => r.MediaTypeId == ScanPreviewRequest.AutoDetect), It.IsAny<CancellationToken>()), Times.Once);
        _imports.Select(i => i.MediaTypeId).Should().BeEquivalentTo([1, 2]);
        _imports.Single(i => i.MediaTypeId == 2).Groups.Select(g => g.Name).Should().Equal("Heat");
        _imports.Single(i => i.MediaTypeId == 1).Groups.Select(g => g.Name).Should().Equal("Show");
    }

    [Fact]
    public async Task EachGroupIsHeldToTheThresholdOfTheTypeItLandedIn()
    {
        var folder = new ScanFolder { Id = 1, Path = "E:/in", MediaTypeId = null, IsEnabled = true };
        _scan.Setup(s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanGroupResult
            {
                Groups =
                [
                    Group("Fine Movie", 2, "movies", 0.80),   // 80 >= movies' 75
                    Group("Weak Show", 1, "tv", 0.80),         // 80 <  tv's 90
                ],
            });

        await Service(folder).ExecuteAsync(CancellationToken.None);

        _imports.Should().ContainSingle().Which.Groups.Select(g => g.Name).Should().Equal("Fine Movie");
    }

    [Fact]
    public async Task AFolderWithAType_IsScannedAndImportedAsBefore()
    {
        var folder = new ScanFolder { Id = 1, Path = "E:/movies", MediaTypeId = 2, IsEnabled = true, MediaType = _db.MediaTypes.Find(2) };
        _scan.Setup(s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanGroupResult { Groups = [new ScanGroup { Name = "Heat", GroupKey = "heat", ConfidenceScore = 0.8, Files = ["E:/movies/Heat.mkv"] }] });

        await Service(folder).ExecuteAsync(CancellationToken.None);

        _scan.Verify(s => s.PreviewGroupedAsync(It.Is<ScanPreviewRequest>(r => r.MediaTypeId == 2), It.IsAny<CancellationToken>()), Times.Once);
        _imports.Should().ContainSingle().Which.MediaTypeId.Should().Be(2);
    }

    [Theory]
    [InlineData(true, null, true)]      // the folder says yes
    [InlineData(false, "true", false)]  // the folder says no, whatever the global setting
    [InlineData(null, "true", true)]    // the folder follows the global setting
    [InlineData(null, "false", false)]
    [InlineData(null, null, false)]     // nothing set anywhere: off
    public async Task RelatedFilesAreRemembered_AsTheFolderOrTheGlobalSettingSays(bool? folderChoice, string? globalSetting, bool expected)
    {
        if (globalSetting is not null)
            _db.AppSettings.Add(new AppSetting { Key = ScheduledScanService.BundleRelatedFilesKey, Value = globalSetting });
        await _db.SaveChangesAsync();
        var folder = new ScanFolder { Id = 1, Path = "E:/movies", MediaTypeId = 2, IsEnabled = true, BundleRelatedFiles = folderChoice, MediaType = _db.MediaTypes.Find(2) };
        _scan.Setup(s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanGroupResult { Groups = [new ScanGroup { Name = "Heat", GroupKey = "heat", ConfidenceScore = 0.9, Files = ["E:/movies/Heat.mkv"] }] });

        await Service(folder).ExecuteAsync(CancellationToken.None);

        _imports.Should().ContainSingle().Which.BundleRelatedFiles.Should().Be(expected);
    }

    [Fact]
    public async Task EveryTypeThatGotNewItems_IsSignalledToKodi()
    {
        var folder = new ScanFolder { Id = 1, Path = "E:/in", MediaTypeId = null, IsEnabled = true };
        _scan.Setup(s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanGroupResult { Groups = [Group("Heat", 2, "movies", 0.8), Group("Show", 1, "tv", 0.95)] });

        await Service(folder).ExecuteAsync(CancellationToken.None);

        _kodi.Verify(k => k.SignalNewContentAsync("movies", It.IsAny<CancellationToken>()), Times.Once);
        _kodi.Verify(k => k.SignalNewContentAsync("tv", It.IsAny<CancellationToken>()), Times.Once);
    }
}

public class ImportGroupingTests
{
    private static ScanGroupImport G(string name, int? type = null) => new(name, null, null, [], [], null, null, null, type);

    [Fact]
    public void GroupsOfOneType_AreOneRequest_ExactlyAsBefore()
    {
        var requests = ImportGrouping.ByType([G("a"), G("b")], 2, bundleRelatedFiles: true);

        var one = requests.Should().ContainSingle().Subject;
        one.MediaTypeId.Should().Be(2);
        one.BundleRelatedFiles.Should().BeTrue();
        one.Groups.Should().HaveCount(2);
    }

    [Fact]
    public void GroupsCarryingTheirOwnType_AreSplitByIt_AndOthersTakeTheDefault()
    {
        var requests = ImportGrouping.ByType([G("a", 1), G("b", 2), G("c", 1), G("d")], 3, bundleRelatedFiles: false);

        requests.Select(r => r.MediaTypeId).Should().BeEquivalentTo([1, 2, 3]);
        requests.Single(r => r.MediaTypeId == 1).Groups.Select(g => g.Name).Should().Equal("a", "c");
        requests.Single(r => r.MediaTypeId == 3).Groups.Select(g => g.Name).Should().Equal("d");
    }

    [Fact]
    public void NothingToImport_IsNoRequests() =>
        ImportGrouping.ByType([], 2, false).Should().BeEmpty();
}
