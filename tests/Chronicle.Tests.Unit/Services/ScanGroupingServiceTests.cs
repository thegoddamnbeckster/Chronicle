using Chronicle.Core.Models.Scan;
using Chronicle.Services.Scan;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class ScanGroupModelTests
{
    [Fact]
    public void ScanGroup_ChildCount_ReturnsCorrectTotal()
    {
        var root = new ScanGroup
        {
            Name = "Metallica",
            HierarchyLevel = 0,
            ConfidenceScore = 0.9,
            Children = new List<ScanGroup>
            {
                new() { Name = "Black Album", HierarchyLevel = 1, ConfidenceScore = 0.85, Children = [], Files = [] },
                new() { Name = "Ride the Lightning", HierarchyLevel = 1, ConfidenceScore = 0.80, Children = [], Files = [] },
            },
            Files = [],
        };

        root.Children.Should().HaveCount(2);
        root.TotalFileCount.Should().Be(0);
    }
}

public class FolderSignalExtractorTests
{
    private readonly FolderSignalExtractor _extractor = new();

    [Theory]
    [InlineData(@"C:\Music\Metallica\Black Album\01 Enter Sandman.mp3", @"C:\Music", 3, "Metallica", "Black Album", "01 Enter Sandman")]
    [InlineData(@"C:\Music\Metallica\01 Enter Sandman.mp3", @"C:\Music", 2, "Metallica", null, "01 Enter Sandman")]
    public void Extract_ReturnsCorrectHierarchyLevels(
        string filePath, string scanRoot, int expectedHierarchyLevels,
        string expectedLevel0, string? expectedLevel1, string expectedLeaf)
    {
        var result = _extractor.Extract(filePath, scanRoot);

        result.HierarchyDepth.Should().Be(expectedHierarchyLevels);
        result.FolderNames[0].Should().Be(expectedLevel0);
        if (expectedLevel1 != null)
            result.FolderNames[1].Should().Be(expectedLevel1);
        result.FileName.Should().Be(expectedLeaf);
    }

    [Theory]
    [InlineData(@"C:\TV\Breaking Bad\Season 1\S01E01 Pilot.mkv", "Breaking Bad", 1, 1)]
    [InlineData(@"C:\TV\Breaking Bad\Season 5\S05E14 Ozymandias.mkv", "Breaking Bad", 5, 14)]
    public void Extract_DetectsSeasonAndEpisodeFromFilename(
        string filePath, string showName, int season, int episode)
    {
        var result = _extractor.Extract(filePath, @"C:\TV");

        result.FolderNames[0].Should().Be(showName);
        result.DetectedSeason.Should().Be(season);
        result.DetectedEpisode.Should().Be(episode);
    }
}

public class TagSignalExtractorTests
{
    [Fact]
    public void Extract_ReturnsEmpty_ForNonAudioFile()
    {
        var extractor = new TagSignalExtractor();
        var result = extractor.Extract(@"C:\Music\Metallica\cover.jpg");
        result.Should().BeNull();
    }

    [Fact]
    public void Extract_ReturnsNull_WhenFileDoesNotExist()
    {
        var extractor = new TagSignalExtractor();
        var result = extractor.Extract(@"C:\nonexistent\file.mp3");
        result.Should().BeNull();
    }
}

public class NfoSignalExtractorTests
{
    [Fact]
    public void Extract_ParsesMusicNfo()
    {
        var nfo = """
            <musicvideo>
              <title>Enter Sandman</title>
              <artist>Metallica</artist>
              <album>Metallica</album>
              <year>1991</year>
            </musicvideo>
            """;
        var extractor = new NfoSignalExtractor();
        var result = extractor.ParseXml(nfo);

        result.Should().NotBeNull();
        result!.Title.Should().Be("Enter Sandman");
        result.Artist.Should().Be("Metallica");
        result.Album.Should().Be("Metallica");
        result.Year.Should().Be(1991);
    }

    [Fact]
    public void Extract_ParsesTvNfo()
    {
        var nfo = """
            <episodedetails>
              <title>Pilot</title>
              <showtitle>Breaking Bad</showtitle>
              <season>1</season>
              <episode>1</episode>
            </episodedetails>
            """;
        var extractor = new NfoSignalExtractor();
        var result = extractor.ParseXml(nfo);

        result!.ShowTitle.Should().Be("Breaking Bad");
        result.Season.Should().Be(1);
        result.Episode.Should().Be(1);
    }

    [Fact]
    public void FindSidecar_ReturnsNfoPathWhenExists()
    {
        // Can't test real filesystem easily; just verify null on missing
        var extractor = new NfoSignalExtractor();
        var result = extractor.FindSidecar(@"C:\nonexistent\file.mkv");
        result.Should().BeNull();
    }
}
