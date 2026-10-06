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

    [Theory]
    [InlineData("Flight Risk (2025).mkv", 2025)]
    [InlineData(@"F:\Movies\Coherence (2014)\Coherence (2014).mkv", 2014)]
    [InlineData("Movie [1999].mkv", 1999)]
    [InlineData("2001 A Space Odyssey (1968).mkv", 1968)]      // the last bracketed year, not a number in the title
    [InlineData("Blade Runner 2049.mkv", null)]                 // an unbracketed number is not a release year
    [InlineData("No Year.mkv", null)]
    [InlineData("Far Future (2999).mkv", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void YearInFileName(string? path, int? expected) =>
        FilePathHelper.YearInFileName(path).Should().Be(expected);
}
