using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Core.Helpers;

public class TvSeasonIndexTests
{
    private static MediaItem Season(int id, int? number) =>
        new() { Id = id, Name = $"Season {number}", HierarchyLevel = 1, Number = number };

    [Fact]
    public void Build_DistinctSeasonNumbers_IndexesEachRow()
    {
        var s1 = Season(10, 1);
        var s2 = Season(20, 2);
        var episodes = new Dictionary<int, HashSet<int>>
        {
            [10] = [1, 2, 3],
            [20] = [1],
        };

        var index = TvSeasonIndex.Build([s1, s2], episodes);

        index.ContainerByNumber[1].Should().BeSameAs(s1);
        index.ContainerByNumber[2].Should().BeSameAs(s2);
        index.EpisodeNumbersBySeasonNumber[1].Should().BeEquivalentTo([1, 2, 3]);
        index.EpisodeNumbersBySeasonNumber[2].Should().BeEquivalentTo([1]);
        index.DuplicatedSeasonNumbers.Should().BeEmpty();
    }

    // Confirmed live (2026-10-02): Big Brother (US) had two "Season 1" rows after the UK show
    // was merged into it, and the old ToDictionary threw on the second one.
    [Fact]
    public void Build_DuplicateSeasonNumber_DoesNotThrowAndPicksOldestRowAsContainer()
    {
        var newer = Season(763256, 1);
        var older = Season(435190, 1);
        var episodes = new Dictionary<int, HashSet<int>>
        {
            [435190] = [1, 2, 3],
            [763256] = [3, 4],
        };

        var index = TvSeasonIndex.Build([newer, older], episodes);

        index.ContainerByNumber[1].Should().BeSameAs(older);
        index.EpisodeNumbersBySeasonNumber[1].Should().BeEquivalentTo([1, 2, 3, 4],
            "an episode present only in the duplicate row must still count as known");
        index.DuplicatedSeasonNumbers.Should().Equal(1);
    }

    [Fact]
    public void Build_SeasonWithNoEpisodes_HasEmptySetNotMissingKey()
    {
        var index = TvSeasonIndex.Build([Season(5, 0)], new Dictionary<int, HashSet<int>>());

        index.ContainerByNumber.Should().ContainKey(0);
        index.EpisodeNumbersBySeasonNumber[0].Should().BeEmpty();
    }

    [Fact]
    public void Build_NullSeasonNumber_IsIgnored()
    {
        var index = TvSeasonIndex.Build([Season(1, null), Season(2, 3)], new Dictionary<int, HashSet<int>>());

        index.ContainerByNumber.Keys.Should().Equal(3);
    }

    [Fact]
    public void Build_ListsEveryDuplicatedNumberAscending()
    {
        var index = TvSeasonIndex.Build(
            [Season(1, 17), Season(2, 0), Season(3, 17), Season(4, 0), Season(5, 4)],
            new Dictionary<int, HashSet<int>>());

        index.DuplicatedSeasonNumbers.Should().Equal(0, 17);
    }
}
