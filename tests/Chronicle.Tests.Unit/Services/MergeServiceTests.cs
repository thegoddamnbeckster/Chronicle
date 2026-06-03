using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class MergeServiceTests
{
    [Theory]
    [InlineData("James S. A. Corey", "James S.A. Corey", true)]   // differ by punctuation
    [InlineData("Brandon Sanderson", "brandon sanderson", false)]  // same normalized — no AKA needed
    [InlineData("Brandon Sanderson", "Patrick Rothfuss",  true)]   // genuinely different — AKA needed
    [InlineData("Abbey Road",        "Abbey Road",        false)]  // identical — no AKA needed
    public void NamesRequireAka_VariousInputs_CorrectResult(string winner, string loser, bool expected)
    {
        MergeService.NamesRequireAka(winner, loser).Should().Be(expected);
    }
}
