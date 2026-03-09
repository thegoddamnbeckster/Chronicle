using Chronicle.Services;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class FileScanServiceHierarchyTests
{
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
    public void GroupFilesForHierarchyImport_NullSeasonNumber_GroupsIntoSeasonZero()
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
        Assert.True(groups[0].Seasons.ContainsKey(0)); // 0 = Specials
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
