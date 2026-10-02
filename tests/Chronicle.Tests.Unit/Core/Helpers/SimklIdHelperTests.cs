using Chronicle.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Core.Helpers;

public class SimklIdHelperTests
{
    [Theory]
    [InlineData("simkl:6347")]
    [InlineData("SIMKL:100144")]
    public void IsUntyped_BareId_ReturnsTrue(string id) => SimklIdHelper.IsUntyped(id).Should().BeTrue();

    [Theory]
    [InlineData("simkl:tv:6347")]
    [InlineData("simkl:movie:1013592")]
    [InlineData("simkl:anime:42")]
    [InlineData("simkl:1003094:s1e2")]
    [InlineData("tmdb:6347")]
    [InlineData("")]
    [InlineData(null)]
    public void IsUntyped_TypedEpisodeOrOtherId_ReturnsFalse(string? id) => SimklIdHelper.IsUntyped(id).Should().BeFalse();
}
