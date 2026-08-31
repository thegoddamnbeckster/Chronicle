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

    // Root-caused a real duplicate (2026-08-31): NormalizeName's own doc comment already
    // documents these three as producing DIFFERENT results, which is exactly what let two
    // MediaItems for the same audiobook author ("James S. A. Corey" vs "James S.A. Corey")
    // coexist. NormalizeNameLoose is the additional, stricter fallback comparison that
    // collapses all three variants to the same string.
    [Theory]
    [InlineData("James S. A. Corey",  "jamessacorey")]
    [InlineData("James S.A. Corey",   "jamessacorey")]
    [InlineData("James S.A.Corey",    "jamessacorey")]
    [InlineData("Brandon Sanderson",  "brandonsanderson")]
    [InlineData("",                   "")]
    [InlineData(null,                 "")]
    public void NormalizeNameLoose_CollapsesSpacingAroundInitials(string? input, string expected)
    {
        MediaItemNormalizer.NormalizeNameLoose(input).Should().Be(expected);
    }
}
