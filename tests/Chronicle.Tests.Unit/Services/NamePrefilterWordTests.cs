using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class NamePrefilterWordTests
{
    [Theory]
    [InlineData("The Mouse Trap (1932)", "mouse")]
    [InlineData("Movie - Subtitle", "subtitle")]
    [InlineData("Dogma (film)", "dogma")]
    [InlineData("Up", null)]                // nothing long enough to narrow on
    [InlineData("Beyoncé", "beyonc")]       // the accented letter splits the word; the prefix still narrows safely
    [InlineData("東京", null)]              // no ASCII word at all: search everything
    public void PicksALongWordEveryVariantContains(string title, string? expected)
    {
        var variants = FileScanService.TitleMatchVariantsForTest(title);
        FileScanService.NamePrefilterWord(variants).Should().Be(expected);
    }

    [Fact]
    public void AWordThatOnlySomeVariantsContain_IsNeverChosen()
    {
        // Variants of "Dogma (film)": the literal one has "film", the parenthetical-stripped one does not.
        var variants = FileScanService.TitleMatchVariantsForTest("Dogma (film)");
        variants.Should().Contain("Dogma");
        FileScanService.NamePrefilterWord(variants).Should().NotBe("film");
    }
}
