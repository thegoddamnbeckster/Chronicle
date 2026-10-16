using Chronicle.Core.Helpers;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Helpers;

public class ExternalIdComparisonTests
{
    [Theory]
    [InlineData("deezer", "album:1", "deezer", "album:2", true)]            // same kind, different ids: different albums
    [InlineData("deezer", "album:1", "deezer", "ALBUM:1", false)]           // same id
    [InlineData("fanarttv", "album:abc/def", "fanarttv", "artist:abc/noRelease:X", false)] // different kinds say nothing
    [InlineData("tmdb", "movie:550", "tmdb", "tv:550", false)]
    [InlineData("imdb", "tt0111161", "imdb", "tt0068646", true)]            // plain ids are always comparable
    [InlineData("imdb", "tt0111161", "imdb", "imdb:tt0068646", true)]       // plain vs kinded: comparable
    [InlineData("tmdb", "movie:1", "imdb", "movie:2", false)]               // different providers never conflict
    [InlineData("Hardcover", "427962", "hardcover", "452060", true)]
    public void Conflicts(string sourceA, string idA, string sourceB, string idB, bool expected) =>
        ExternalIdComparison.Conflicts(sourceA, idA, sourceB, idB).Should().Be(expected);

    [Theory]
    [InlineData("album:613526", "album")]
    [InlineData("tt0111161", null)]
    [InlineData(":x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void KindOf(string? id, string? expected) => ExternalIdComparison.KindOf(id).Should().Be(expected);
}
