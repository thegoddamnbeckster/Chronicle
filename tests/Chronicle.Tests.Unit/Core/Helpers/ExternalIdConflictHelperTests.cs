using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Core.Helpers;

public class ExternalIdConflictHelperTests
{
    private static MediaExternalId Id(string source, string value) => new() { Source = source, ExternalId = value };

    // Big Brother US vs UK (2026-10-02): same title and year, different TMDB/TVmaze ids.
    [Fact]
    public void HasConflict_SameSourceDifferentValue_IsTrue()
    {
        ExternalIdConflictHelper.HasConflict(
            [Id("tmdb", "tv:10160"), Id("tvmaze", "show:1453")],
            [Id("tmdb", "tv:11366"), Id("tvmaze", "show:1514")]).Should().BeTrue();
    }

    [Fact]
    public void HasConflict_OneSharedValueForTheSource_IsFalse()
    {
        ExternalIdConflictHelper.HasConflict(
            [Id("tmdb", "tv:10160"), Id("tmdb", "tv:11366")],
            [Id("tmdb", "tv:11366")]).Should().BeFalse();
    }

    [Fact]
    public void HasConflict_NoSourceInCommon_IsFalse()
    {
        ExternalIdConflictHelper.HasConflict([Id("tmdb", "tv:1")], [Id("simkl", "simkl:9")]).Should().BeFalse();
    }

    [Fact]
    public void HasConflict_IsCaseInsensitive()
    {
        ExternalIdConflictHelper.HasConflict([Id("TMDB", "TV:1")], [Id("tmdb", "tv:1")]).Should().BeFalse();
    }

    [Fact]
    public void HasConflict_IgnoresFanartSentinelsAndNonIdentityIds()
    {
        ExternalIdConflictHelper.HasConflict(
            [Id("fanarttv", "artist:a"), Id("tmdb", "__suppress__"), Id("chronicle", "manual-collection-member")],
            [Id("fanarttv", "artist:b"), Id("tmdb", "tv:5"), Id("chronicle", "manual-collection-member")]).Should().BeFalse();
    }
}
