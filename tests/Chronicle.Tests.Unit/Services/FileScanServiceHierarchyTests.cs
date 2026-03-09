using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class FileScanServiceHierarchyTests
{
    private static ChronicleDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(opts);
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
    }
}
