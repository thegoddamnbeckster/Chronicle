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
