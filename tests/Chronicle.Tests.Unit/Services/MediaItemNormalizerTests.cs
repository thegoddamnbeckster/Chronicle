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
    // A quoted nickname must strip out entirely (not just its quote characters), so this
    // matches the same person's plain "Michael Smith" credit from another source. See
    // PersonResolutionServiceTests for the end-to-end dedup this backs.
    [InlineData("Michael \"Mike\" Smith", "michael smith")]
    [InlineData("Michael “Mike” Smith", "michael smith")] // curly quotes
    [InlineData("O'Brien",            "obrien")] // apostrophe alone must NOT be treated as a nickname delimiter
    // Root-caused live (2026-09-15): "Jason Acuña" and "Jason 'Wee Man' Acuña" (same real
    // person, same 1973-05-16 birthdate) normalized to two different strings because the
    // single-quoted nickname was never stripped -- only double-quoted ones were.
    [InlineData("Jason 'Wee Man' Acuña", "jason acuña")]
    [InlineData("D'Angelo",           "dangelo")] // real apostrophe, no nickname anywhere
    // A single-quoted nickname sitting right at the start of the string -- the whitespace-based
    // boundary check must not require an actual space character that doesn't exist there.
    [InlineData("'Stone Cold' Steve Austin", "steve austin")]
    // Both a real apostrophe AND a single-quoted nickname in the same name -- the real
    // apostrophe (no surrounding whitespace) must survive while the nickname (surrounded by
    // whitespace on both sides) still strips.
    [InlineData("O'Brien 'Big O' Smith", "obrien smith")]
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
    [InlineData("Michael \"Mike\" Smith", "michaelsmith")]
    [InlineData("Jason 'Wee Man' Acuña", "jasonacuna")] // also exercises diacritic folding below
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

    // Root-caused a real duplicate (2026-09-15): "Alex Acuña" and "Alex Acuna" -- same real
    // person, same 1944-12-12 birthdate, one source's credit simply missing the tilde --
    // normalized to different strings under NormalizeName (which reconciles different
    // ENCODINGS of an accented character, per the test above, but never removes a diacritic
    // that's genuinely absent from one side). NormalizeNameLoose now folds diacritics too, so
    // PersonResolutionService's own loose-name fallback match (Step 2b) catches this pair.
    [Theory]
    [InlineData("Alex Acuña",   "alexacuna")]
    [InlineData("Alex Acuna",   "alexacuna")]
    [InlineData("Björgvin Arnarson", "bjorgvinarnarson")]
    [InlineData("Plain Name",   "plainname")] // no diacritics at all -- must pass through unchanged
    public void NormalizeNameLoose_FoldsDiacritics(string input, string expected)
    {
        MediaItemNormalizer.NormalizeNameLoose(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeName_DoesNotFoldDiacritics()
    {
        // Diacritic folding is deliberately scoped to the Loose fallback tier only -- see
        // NormalizeNameLoose's own doc for why widening the primary NormalizeName itself would
        // need a backfill migration rather than just a method-body change.
        MediaItemNormalizer.NormalizeName("Alex Acuña").Should().Be("alex acuña");
        MediaItemNormalizer.NormalizeName("Alex Acuna").Should().Be("alex acuna");
    }
}
