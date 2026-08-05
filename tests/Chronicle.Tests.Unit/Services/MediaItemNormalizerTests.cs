using Chronicle.Core.Helpers;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class MediaItemNormalizerTests
{
    [Theory]
    [InlineData("James S. A. Corey",  "james s a corey")]
    [InlineData("James S.A. Corey",   "james sa corey")]
    [InlineData("James S.A.Corey",    "james sacorey")]
    [InlineData("Brandon Sanderson",  "brandon sanderson")]
    [InlineData("The Way of Kings",   "the way of kings")]
    [InlineData("Abbey Road",         "abbey road")]
    [InlineData("",                   "")]
    [InlineData(null,                 "")]
    public void NormalizeName_VariousInputs_CorrectResult(string? input, string expected)
    {
        MediaItemNormalizer.NormalizeName(input).Should().Be(expected);
    }
}
