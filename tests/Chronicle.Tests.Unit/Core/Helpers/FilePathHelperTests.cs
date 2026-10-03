using Chronicle.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Core.Helpers;

public class FilePathHelperTests
{
    [Theory]
    [InlineData(@"F:\Videos\Movies\Some Movie (2020)\Some Movie (2020).mkv", "Some Movie (2020).mkv")]   // a Windows path
    [InlineData("/media/movies/Some Movie (2020)/Some Movie (2020).mkv", "Some Movie (2020).mkv")]          // a POSIX path
    [InlineData(@"smb://10.0.0.162/Video1/Movies/X (2020)/X (2020).mkv", "X (2020).mkv")]                    // a Kodi URL
    [InlineData(@"F:\Videos/Movies\Mixed (2020)/Mixed (2020).mkv", "Mixed (2020).mkv")]                    // mixed separators
    [InlineData("Plain.mkv", "Plain.mkv")]                                                                    // no separator
    [InlineData(@"F:\Videos\Movies\", "")]                                                                 // trailing separator, like Path.GetFileName
    [InlineData("/media/movies/", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void GetFileName_GivesTheSameAnswerOnEveryOs(string? path, string expected)
    {
        FilePathHelper.GetFileName(path).Should().Be(expected);
    }

    [Fact]
    public void GetFileName_AgreesWithPathGetFileName_ForPathsInTheCurrentOsStyle()
    {
        var path = Path.Combine("media", "movies", "Some Movie (2020)", "Some Movie (2020).mkv");

        FilePathHelper.GetFileName(path).Should().Be(Path.GetFileName(path));
    }
}
