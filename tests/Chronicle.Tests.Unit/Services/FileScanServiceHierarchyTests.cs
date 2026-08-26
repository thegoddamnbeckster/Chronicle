using Chronicle.Plugins.Models;
using Chronicle.Services;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class FileScanServiceHierarchyTests
{
    // ── CollapseAudiobooksToFolders ───────────────────────────────────────────

    [Fact]
    public void CollapseAudiobooksToFolders_SumsDurationsAcrossGroup()
    {
        var root       = @"C:\Books\Brandon Sanderson";
        var bookFolder = root + @"\Stormlight - 1 - (2010) - The Way of Kings";
        var files = new List<ScannedFile>
        {
            new() { FilePath = bookFolder + @"\01.mp3", DurationSeconds = 1800,
                    AudioAlbum = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson" },
            new() { FilePath = bookFolder + @"\02.mp3", DurationSeconds = 2100,
                    AudioAlbum = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson" },
            new() { FilePath = bookFolder + @"\03.mp3", DurationSeconds = 900,
                    AudioAlbum = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson" },
        };

        var result = FileScanService.CollapseAudiobooksToFoldersForTest(files, root);

        Assert.Single(result);
        Assert.Equal(4800, result[0].TotalDurationSeconds); // 1800+2100+900
    }

    [Fact]
    public void CollapseAudiobooksToFolders_SingleRootLevelFile_SetsTotal()
    {
        var root = @"C:\Books";
        var files = new List<ScannedFile>
        {
            new() { FilePath = root + @"\Elantris.mp3", DurationSeconds = 3600 },
        };

        var result = FileScanService.CollapseAudiobooksToFoldersForTest(files, root);

        Assert.Single(result);
        Assert.Equal(3600, result[0].TotalDurationSeconds);
    }

    [Fact]
    public void CollapseAudiobooksToFolders_BookFolderUnderAuthor_SetsAuthorFolderPath()
    {
        var libraryRoot = @"C:\Books";
        var authorFolder = libraryRoot + @"\Brandon Sanderson";
        var bookFolder    = authorFolder + @"\Stormlight - 1 - (2010) - The Way of Kings";
        var files = new List<ScannedFile>
        {
            new() { FilePath = bookFolder + @"\01.mp3", DurationSeconds = 1800,
                    AudioAlbum = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson" },
        };

        var result = FileScanService.CollapseAudiobooksToFoldersForTest(files, libraryRoot);

        Assert.Single(result);
        Assert.Equal(authorFolder, result[0].AuthorFolderPath);
    }

    [Fact]
    public void CollapseAudiobooksToFolders_BookFolderAtScanRoot_LeavesAuthorFolderPathNull()
    {
        var root       = @"C:\Books\Brandon Sanderson";
        var bookFolder = root; // book folder IS the scan root — no author level above it
        var files = new List<ScannedFile>
        {
            new() { FilePath = bookFolder + @"\01.mp3", DurationSeconds = 1800 },
        };

        var result = FileScanService.CollapseAudiobooksToFoldersForTest(files, root);

        Assert.Single(result);
        Assert.Null(result[0].AuthorFolderPath);
    }

    // ── GroupAudiobooksByAuthorAndSeries ──────────────────────────────────────

    [Fact]
    public void GroupAudiobooksByAuthorAndSeries_CreatesAuthorSeriesBookTree()
    {
        var files = new List<ScannedFile>
        {
            new() { FilePath = @"C:\Books\B Sanderson\SA-1-(2010)-Way",
                    ParsedTitle = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson",
                    AudioGrouping = "Stormlight Archive", ParsedYear = 2010, TotalDurationSeconds = 3600 },
            new() { FilePath = @"C:\Books\B Sanderson\SA-2-(2014)-Words",
                    ParsedTitle = "Words of Radiance", AudioAlbumArtist = "Brandon Sanderson",
                    AudioGrouping = "Stormlight Archive", ParsedYear = 2014, TotalDurationSeconds = 4200 },
            new() { FilePath = @"C:\Books\B Sanderson\(2005)-Elantris",
                    ParsedTitle = "Elantris", AudioAlbumArtist = "Brandon Sanderson",
                    AudioGrouping = null, ParsedYear = 2005, TotalDurationSeconds = 1800 },
        };

        var groups = FileScanService.GroupAudiobooksByAuthorAndSeriesForTest(files);

        // One author group
        Assert.Single(groups);
        var author = groups[0];
        Assert.Equal("Brandon Sanderson", author.Name);
        Assert.Equal(0, author.HierarchyLevel);

        // Two children: one series, one standalone book
        Assert.Equal(2, author.Children.Count);

        var series = author.Children.First(c => c.Name == "Stormlight Archive");
        Assert.Equal(1, series.HierarchyLevel);
        Assert.Equal(2, series.Children.Count);

        var standalone = author.Children.First(c => c.Name == "Elantris");
        Assert.Equal(1, standalone.HierarchyLevel);
        Assert.Empty(standalone.Children);
        Assert.Single(standalone.Files);
    }

    [Fact]
    public void GroupAudiobooksByAuthorAndSeries_PropagatesAuthorFolderPathOntoAuthorGroup()
    {
        var files = new List<ScannedFile>
        {
            new() { FilePath = @"C:\Books\B Sanderson\SA-1-(2010)-Way",
                    ParsedTitle = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson",
                    AudioGrouping = "Stormlight Archive", ParsedYear = 2010,
                    AuthorFolderPath = @"C:\Books\B Sanderson" },
        };

        var groups = FileScanService.GroupAudiobooksByAuthorAndSeriesForTest(files);

        Assert.Single(groups);
        Assert.Equal(@"C:\Books\B Sanderson", groups[0].FolderPath);
    }

    [Fact]
    public void GroupAudiobooksByAuthorAndSeries_UnknownAuthor_GroupsUnderUnknown()
    {
        var files = new List<ScannedFile>
        {
            new() { FilePath = @"C:\Books\Mystery\(2020)-Unknown Book",
                    ParsedTitle = "Unknown Book", AudioAlbumArtist = null,
                    AudioGrouping = null, ParsedYear = 2020 },
        };

        var groups = FileScanService.GroupAudiobooksByAuthorAndSeriesForTest(files);

        Assert.Single(groups);
        Assert.Equal("Unknown", groups[0].Name);
        Assert.Single(groups[0].Children);
    }


    [Fact]
    public async Task GroupFilesForHierarchyImport_TvFiles_GroupsByShow()
    {
        // Arrange — three episodes of the same show, two different seasons
        var files = new[]
        {
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "S01E01.mkv", ParsedTitle = "Ep1",
                ShowTitle = "My Show", SeasonNumber = 1, EpisodeNumber = 1
            },
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "S01E02.mkv", ParsedTitle = "Ep2",
                ShowTitle = "My Show", SeasonNumber = 1, EpisodeNumber = 2
            },
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "S02E01.mkv", ParsedTitle = "S2Ep1",
                ShowTitle = "My Show", SeasonNumber = 2, EpisodeNumber = 1
            },
        };

        // Act — group by show then season
        var groups = FileScanService.GroupByShowForTest(files);

        // Assert
        Assert.Single(groups);                                    // one show
        Assert.Equal("My Show", groups[0].ShowTitle);
        Assert.Equal(2, groups[0].Seasons.Count);                // two seasons
        Assert.Equal(2, groups[0].Seasons[1].Episodes.Count);    // S1 has 2 eps
        Assert.Single(groups[0].Seasons[2].Episodes);            // S2 has 1 ep

        await Task.CompletedTask; // suppress async-without-await warning
    }

    [Fact]
    public void GroupFilesForHierarchyImport_CaseInsensitive_MergesShowNames()
    {
        // Titles differing only in case should be merged into one show group
        var files = new[]
        {
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "A.mkv", ParsedTitle = "Ep1",
                ShowTitle = "my show", SeasonNumber = 1, EpisodeNumber = 1
            },
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "B.mkv", ParsedTitle = "Ep2",
                ShowTitle = "MY SHOW", SeasonNumber = 1, EpisodeNumber = 2
            },
        };

        var groups = FileScanService.GroupByShowForTest(files);

        Assert.Single(groups); // both should be in the same show
        Assert.Equal(2, groups[0].Seasons[1].Episodes.Count);
    }

    [Fact]
    public void GroupFilesForHierarchyImport_NullSeasonNumber_GroupsIntoSeasonOne()
    {
        var files = new[]
        {
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "C.mkv", ParsedTitle = "Special",
                ShowTitle = "My Show", SeasonNumber = null, EpisodeNumber = 1
            },
        };

        var groups = FileScanService.GroupByShowForTest(files);

        Assert.Single(groups);
        Assert.True(groups[0].Seasons.ContainsKey(1)); // null season defaults to Season 1
    }

    [Fact]
    public void GroupFilesForHierarchyImport_EmptyShowTitle_IsSkipped()
    {
        var files = new[]
        {
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "D.mkv", ParsedTitle = "", // empty ParsedTitle, no ShowTitle
                ShowTitle = null, SeasonNumber = 1, EpisodeNumber = 1
            },
        };

        var groups = FileScanService.GroupByShowForTest(files);

        Assert.Empty(groups); // file with no title should be skipped
    }

    [Fact]
    public void GroupFilesForHierarchyImport_EmptyInput_ReturnsEmptyList()
    {
        var groups = FileScanService.GroupByShowForTest([]);

        Assert.Empty(groups);
    }
}
