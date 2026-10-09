using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class EpisodeRangeTests
{
    [Theory]
    [InlineData("Star Trek - Enterprise - S01E01-E02 - Broken Bow.mkv", 1, 2)]
    [InlineData("Show S01E01E02.mkv", 1, 2)]
    [InlineData("Show S01E01-02.mkv", 1, 2)]
    [InlineData("Show S01E05-E07 Title.mkv", 5, 7)]
    [InlineData("show.s02e10e11.720p.mkv", 10, 11)]
    public void ReadsTheRangeAFileHolds(string name, int first, int last) =>
        EpisodeRange.Of(name).Should().Be((first, last));

    [Theory]
    [InlineData("Show S01E01.mkv")]
    [InlineData("Show S01E01 Title With 2 Numbers.mkv")]
    [InlineData("Show - 1x01.mkv")]
    [InlineData("S01E01-E40 absurd.mkv")]
    [InlineData("")]
    [InlineData(null)]
    public void ASingleEpisodeOrNonsenseIsNotARange(string? name) => EpisodeRange.Of(name).Should().BeNull();

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void CoversOnlyTheEpisodesInTheRange(int episode, bool expected) =>
        EpisodeRange.Covers("Show S01E01-E02.mkv", episode).Should().Be(expected);
}
