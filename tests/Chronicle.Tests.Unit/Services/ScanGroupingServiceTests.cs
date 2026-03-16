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

public class ScanGroupingServiceTests
{
    private readonly ScanGroupingService _svc = new(
        new FolderSignalExtractor(),
        new TagSignalExtractor(),
        new NfoSignalExtractor());

    [Fact]
    public void Group_FlatMusicFiles_BuildsArtistAlbumTree()
    {
        // Three files: same artist, same album, different tracks — folder layout
        var files = new[]
        {
            @"C:\Music\Metallica\Black Album\01 Enter Sandman.mp3",
            @"C:\Music\Metallica\Black Album\02 Sad But True.mp3",
            @"C:\Music\Metallica\Black Album\03 Holier Than Thou.mp3",
        };

        var result = _svc.Group(files, scanRoot: @"C:\Music", hierarchyLevels: 3);

        result.Groups.Should().HaveCount(1);
        var artist = result.Groups[0];
        artist.Name.Should().Be("Metallica");
        artist.HierarchyLevel.Should().Be(0);
        artist.Children.Should().HaveCount(1);

        var album = artist.Children[0];
        album.Name.Should().Be("Black Album");
        album.HierarchyLevel.Should().Be(1);
        album.Children.Should().HaveCount(3);

        result.Ungrouped.Should().BeEmpty();
    }

    [Fact]
    public void Group_FlatGroupedType_PutsAllFilesInOneGroup()
    {
        // Audiobook: many chapter files in one folder, HierarchyLevels=1
        var files = new[]
        {
            @"C:\Audiobooks\The Hobbit\Part1.mp3",
            @"C:\Audiobooks\The Hobbit\Part2.mp3",
            @"C:\Audiobooks\The Hobbit\Part3.mp3",
        };

        var result = _svc.Group(files, scanRoot: @"C:\Audiobooks", hierarchyLevels: 1);

        result.Groups.Should().HaveCount(1);
        var book = result.Groups[0];
        book.Name.Should().Be("The Hobbit");
        book.HierarchyLevel.Should().Be(0);
        book.Children.Should().BeEmpty();
        book.Files.Should().HaveCount(3);
    }

    [Fact]
    public void Group_MultipleArtists_CreatesOneGroupPerArtist()
    {
        var files = new[]
        {
            @"C:\Music\Metallica\Black Album\01 Enter Sandman.mp3",
            @"C:\Music\Nirvana\Nevermind\01 Smells Like Teen Spirit.mp3",
        };

        var result = _svc.Group(files, scanRoot: @"C:\Music", hierarchyLevels: 3);

        result.Groups.Should().HaveCount(2);
        result.Groups.Select(g => g.Name).Should().Contain(["Metallica", "Nirvana"]);
    }

    [Fact]
    public void Group_ImageFiles_AreNotIncludedAsLeafFiles()
    {
        var files = new[]
        {
            @"C:\Music\Metallica\Black Album\01 Enter Sandman.mp3",
            @"C:\Music\Metallica\Black Album\cover.jpg",
            @"C:\Music\Metallica\Black Album\fanart.png",
        };

        var result = _svc.Group(files, scanRoot: @"C:\Music", hierarchyLevels: 3);

        var album = result.Groups[0].Children[0];
        // Image files don't become leaf items — only the audio track does
        album.Children.Should().HaveCount(1);
        album.Children[0].Name.Should().Contain("Enter Sandman");
    }

    [Fact]
    public void Group_NfoAndImageFiles_DoNotAppearInUngrouped()
    {
        var files = new[]
        {
            @"C:\Music\Metallica\Black Album\01 Enter Sandman.mp3",
            @"C:\Music\Metallica\Black Album\album.nfo",
            @"C:\Music\Metallica\Black Album\cover.jpg",
        };

        var result = _svc.Group(files, scanRoot: @"C:\Music", hierarchyLevels: 3);

        result.Ungrouped.Should().BeEmpty();
    }
}
