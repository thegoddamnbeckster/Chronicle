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

    // Regression test for a real production duplicate (2026-09-03): the same visible name can
    // arrive as either a single precomposed codepoint ("o with diaeresis", NFC) or a base
    // letter plus a combining diaeresis mark (NFD) depending on which provider produced the
    // string -- visually and semantically identical, but plain ToLowerInvariant treats them as
    // completely different strings. "Bjorgvin Arnarson" arrived in both forms from two
    // different sources and got two separate Person records because of exactly this gap.
    // Built from explicit \u escapes rather than a typed literal: a source file can't reliably
    // preserve the byte-level distinction between the two forms once it round-trips through an
    // editor/encoding, so both are spelled out with codepoints here instead.
    private const string PrecomposedName = "Björgvin Arnarson";   // "o with diaeresis" as one codepoint (NFC)
    private const string DecomposedName  = "Björgvin Arnarson";  // "o" + combining diaeresis (NFD)

    [Fact]
    public void NormalizeName_SameCharacterDifferentUnicodeComposition_ProducesSameResult()
    {
        PrecomposedName.Should().NotBe(DecomposedName, "the two raw strings really are byte-different");

        MediaItemNormalizer.NormalizeName(PrecomposedName)
            .Should().Be(MediaItemNormalizer.NormalizeName(DecomposedName));
    }

    [Fact]
    public void NormalizeNameLoose_SameCharacterDifferentUnicodeComposition_ProducesSameResult()
    {
        MediaItemNormalizer.NormalizeNameLoose(PrecomposedName)
            .Should().Be(MediaItemNormalizer.NormalizeNameLoose(DecomposedName));
    }
}
