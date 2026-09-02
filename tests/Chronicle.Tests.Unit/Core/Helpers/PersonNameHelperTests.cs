using Chronicle.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Core.Helpers;

public class PersonNameHelperTests
{
    [Theory]
    [InlineData("Bill Nunn", "Nunn Bill")]
    [InlineData("Chevy Chase", "Chase Chevy")]
    [InlineData("Michael J. Fox", "Fox Michael J.")]
    public void ToLastNameFirstSortKey_PlainTwoOrThreeTokenNames_MovesSurnameFirst(string name, string expected)
    {
        PersonNameHelper.ToLastNameFirstSortKey(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("Guillermo del Toro", "del Toro Guillermo")]
    [InlineData("Ludwig van Beethoven", "van Beethoven Ludwig")]
    public void ToLastNameFirstSortKey_LowercaseParticleBeforeSurname_StaysWithSurname(string name, string expected)
    {
        PersonNameHelper.ToLastNameFirstSortKey(name).Should().Be(expected);
    }

    [Theory]
    // The suffix isn't dropped -- just excluded from acting as the surname itself, so e.g. a
    // "Jr." and "Sr." sharing every other token still sort as distinct people rather than
    // colliding onto the same key.
    [InlineData("Martin Luther King Jr.", "King Martin Luther Jr.")]
    [InlineData("Robert Downey Jr", "Downey Robert Jr")]
    [InlineData("Sammy Davis Jr.", "Davis Sammy Jr.")]
    public void ToLastNameFirstSortKey_GenerationalSuffix_NotTreatedAsSurname(string name, string expected)
    {
        PersonNameHelper.ToLastNameFirstSortKey(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("Madonna")]
    [InlineData("Cher")]
    public void ToLastNameFirstSortKey_SingleToken_ReturnedUnchanged(string name)
    {
        PersonNameHelper.ToLastNameFirstSortKey(name).Should().Be(name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToLastNameFirstSortKey_NullOrBlank_DoesNotThrow(string? name)
    {
        PersonNameHelper.ToLastNameFirstSortKey(name).Should().Be((name ?? string.Empty).Trim());
    }
}
