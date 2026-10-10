using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Chronicle.Services.Scan;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class FileTypeClassifierTests
{
    private const string TvHints = "{\"filePatterns\":[\"(?i)\\\\bS\\\\d{1,2}E\\\\d{1,3}\\\\b\"],\"folderPatterns\":[\"(?i)^season\\\\s*\\\\d+$\"],\"extensions\":[]}";
    private static readonly MediaType Tv = new() { Id = 1, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, ScanHintsJson = TvHints };
    private static readonly MediaType Movies = new() { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, ScanHintsJson = "{\"extensions\":[\".mkv\",\".mp4\"]}" };
    private static readonly MediaType Music = new() { Id = 3, Name = "music", DisplayName = "Music", HierarchyLevels = 3, ScanHintsJson = "{\"extensions\":[\".mp3\",\".flac\"]}" };

    private static FileTypeClassifier Sut(params MediaType[] types) => new(types);

    [Theory]
    [InlineData("C:/x/Show.S01E02.mkv", "tv")]
    [InlineData("C:/x/Show/Season 2/anything.mkv", "tv")]
    [InlineData("C:/x/Heat (1995)/Heat.mkv", "movies")]
    [InlineData("C:/x/Artist/Album/01 Song.mp3", "music")]
    [InlineData("C:/x/Artist/Album/Song.FLAC", "music")]
    public void SortsFilesByWhatTheirTypesSayTheyLookLike(string path, string expected) =>
        Sut(Tv, Movies, Music).Classify(path)!.Name.Should().Be(expected);

    [Fact]
    public void AnEpisodeCode_BeatsTheBroadExtension() =>
        Sut(Tv, Movies).Classify("C:/x/Show.S01E02.mkv")!.Name.Should().Be("tv");

    [Theory]
    [InlineData("C:/x/readme.txt")]
    [InlineData("C:/x/poster.jpg")]
    [InlineData("C:/x/video.xyz")]
    public void FilesNoTypeRecognises_HaveNoType(string path) =>
        Sut(Tv, Movies, Music).Classify(path).Should().BeNull();

    [Fact]
    public void TypesWithoutHints_InactiveOnes_AndTypesWithASpecialScanStyle_NeverTakePart()
    {
        var bare = new MediaType { Id = 9, Name = "comics", DisplayName = "Comics", HierarchyLevels = 1 };
        var off = new MediaType { Id = 8, Name = "off", DisplayName = "Off", IsActive = false, ScanHintsJson = "{\"extensions\":[\".mkv\"]}" };
        var audiobooks = new MediaType { Id = 7, Name = "audiobooks", DisplayName = "Audiobooks", ScanStrategy = "audiobook", ScanHintsJson = "{\"extensions\":[\".mp3\"]}" };

        var sut = Sut(bare, off, audiobooks);

        sut.HasCandidates.Should().BeFalse();
        sut.Classify("C:/x/a.mkv").Should().BeNull();
        sut.Classify("C:/x/a.mp3").Should().BeNull();
    }

    [Fact]
    public void TwoTypesListingTheSameExtension_GoToTheLowerId()
    {
        var a = new MediaType { Id = 5, Name = "a", DisplayName = "A", ScanHintsJson = "{\"extensions\":[\".mkv\"]}" };
        var b = new MediaType { Id = 4, Name = "b", DisplayName = "B", ScanHintsJson = "{\"extensions\":[\".mkv\"]}" };

        Sut(a, b).Classify("C:/x/a.mkv")!.Name.Should().Be("b");
    }

    [Fact]
    public void AUserMadeTypeWithHints_IsPickedWithoutAnyCodeChange()
    {
        var anime = new MediaType { Id = 6, Name = "anime", DisplayName = "Anime", HierarchyLevels = 3, ScanHintsJson = "{\"filePatterns\":[\"(?i)^\\\\[[^\\\\]]+\\\\]\"],\"extensions\":[]}" };

        Sut(Tv, Movies, anime).Classify("C:/x/[Group] Show - 05.mkv")!.Name.Should().Be("anime");
    }

    [Fact]
    public void Partition_PutsSidecarsWithTheFilesAroundThem()
    {
        var files = new[]
        {
            "C:/lib/Heat (1995)/Heat.mkv", "C:/lib/Heat (1995)/Heat.en.srt", "C:/lib/Heat (1995)/poster.jpg",
            "C:/lib/Show/Season 1/Show.S01E01.mkv", "C:/lib/Show/Season 1/Show.S01E01.srt", "C:/lib/Show/poster.jpg",
            "C:/lib/stray.txt",
        };

        var parts = Sut(Tv, Movies, Music).Partition(files);

        parts[Movies].Should().Contain("C:/lib/Heat (1995)/Heat.en.srt").And.Contain("C:/lib/Heat (1995)/poster.jpg");
        parts[Tv].Should().Contain("C:/lib/Show/Season 1/Show.S01E01.srt");
        parts.Values.SelectMany(v => v).Should().NotContain("C:/lib/stray.txt");
    }

    [Fact]
    public void Partition_ASidecarGoesWithTheMajorityOfItsFolder()
    {
        var files = new[] { "C:/mix/a.mp3", "C:/mix/b.mp3", "C:/mix/c.mkv", "C:/mix/cover.jpg" };

        var parts = Sut(Tv, Movies, Music).Partition(files);

        parts[Music].Should().Contain("C:/mix/cover.jpg");
        parts[Movies].Should().NotContain("C:/mix/cover.jpg");
    }
}

public sealed class AutoDetectPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chronicle-auto-" + Guid.NewGuid().ToString("N"));

    public AutoDetectPreviewTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* scratch */ } }

    private void Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
    }

    private static async Task<(ChronicleDbContext Db, FileScanService Service)> SetUpAsync(bool withHints = true)
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        string? Hints(string json) => withHints ? json : null;
        db.MediaTypes.AddRange(
            new MediaType { Id = 1, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow,
                ScanHintsJson = Hints("{\"filePatterns\":[\"(?i)\\\\bS\\\\d{1,2}E\\\\d{1,3}\\\\b\"],\"folderPatterns\":[\"(?i)^season\\\\s*\\\\d+$\"]}") },
            new MediaType { Id = 2, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow,
                ScanHintsJson = Hints("{\"extensions\":[\".mkv\",\".mp4\"]}") },
            new MediaType { Id = 3, Name = "music", DisplayName = "Music", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow,
                ScanHintsJson = Hints("{\"extensions\":[\".mp3\",\".flac\"]}") });
        await db.SaveChangesAsync();

        var grouping = new ScanGroupingService(new FolderSignalExtractor(), new TagSignalExtractor());
        var registry = new Mock<IPluginRegistry>();
        return (db, new FileScanService(db, registry.Object, null!, new ScanProgressService(), new ImportProgressService(), grouping));
    }

    [Fact]
    public async Task AMixedFolder_ComesOutAsGroupsOfEachKind_EachTaggedWithItsType()
    {
        Touch("Heat (1995)/Heat (1995).mkv");
        Touch("Heat (1995)/Heat (1995).en.srt");
        Touch("Show/Season 1/Show - S01E01.mkv");
        Touch("Show/Season 1/Show - S01E02.mkv");
        Touch("Metallica/Black Album/01 - Enter Sandman.mp3");
        var (db, service) = await SetUpAsync();
        await using var _ = db;

        var result = await service.PreviewGroupedAsync(new ScanPreviewRequest(_dir, true, ScanPreviewRequest.AutoDetect));

        var byType = result.Groups.ToDictionary(g => g.MediaTypeName!, g => g);
        byType.Keys.Should().BeEquivalentTo("Movies", "TV", "Music");
        byType["Movies"].Name.Should().Be("Heat (1995)");
        byType["Movies"].RelatedFiles.Should().ContainSingle().Which.Should().EndWith(".srt");
        byType["TV"].Name.Should().Be("Show");
        byType["TV"].TotalFileCount.Should().Be(2);
        byType["Music"].Name.Should().Be("Metallica");
        result.Groups.Should().OnlyContain(g => g.MediaTypeId != null);
        result.TotalFiles.Should().Be(5);
    }

    [Fact]
    public async Task WithNoHintsAnywhere_TheScanSaysWhatToDoInsteadOfGuessing()
    {
        Touch("a/b.mkv");
        var (db, service) = await SetUpAsync(withHints: false);
        await using var _ = db;

        var act = () => service.PreviewGroupedAsync(new ScanPreviewRequest(_dir, true, ScanPreviewRequest.AutoDetect));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("scan hints");
    }

    [Fact]
    public async Task AChosenType_StillScansExactlyAsBefore()
    {
        Touch("Heat (1995)/Heat (1995).mkv");
        Touch("Show/Season 1/Show - S01E01.mkv");
        var (db, service) = await SetUpAsync();
        await using var _ = db;

        var result = await service.PreviewGroupedAsync(new ScanPreviewRequest(_dir, true, 2));

        result.Groups.Should().OnlyContain(g => g.MediaTypeId == null);
    }
}
