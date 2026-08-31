using Chronicle.Core.Helpers;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Core.Helpers;

// Regex \d matches the whole Unicode Nd category (not just ASCII 0-9), so a title like
// "今際の国のアリス（１４）" (Alice in Borderland vol. 14, fullwidth digits) used to make
// \d+ match "１４" and then int.Parse throw FormatException on it -- confirmed live
// (2026-08-30) crashing every metadata-enrichment attempt for this exact item, and present
// as the same pattern (regex \d capture group -> int.Parse) in the file-scanner's
// filename/folder-name parsing too.
public class DigitParsingHelperTests
{
    [Theory]
    [InlineData("14", 14)]
    [InlineData("1", 1)]
    [InlineData("１４", 14)]     // fullwidth digits -- the actual reported crash
    [InlineData("１５", 15)]
    [InlineData("０７", 7)]      // fullwidth, leading zero
    [InlineData("0", 0)]
    public void TryParseDigits_ParsesAsciiAndFullwidthDigits(string digits, int expected)
    {
        DigitParsingHelper.TryParseDigits(digits, out var number).Should().BeTrue();
        number.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12a")]   // not purely digits -- a regex \d+ capture group would never
                            // produce this, but the helper must not throw regardless
    [InlineData("a")]
    public void TryParseDigits_NonDigitInput_ReturnsFalse(string? digits)
    {
        DigitParsingHelper.TryParseDigits(digits!, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseDigits_MidStringFailure_LeavesOutParamAtZero()
    {
        // Matches int.TryParse's own contract: on failure the out param is the default (0),
        // never a partial value from however far accumulation got ("12a" must not leave
        // number == 12) -- a caller that forgets to check the bool return should still see
        // an obviously-wrong 0, not a plausible-looking wrong number.
        DigitParsingHelper.TryParseDigits("12a", out var number).Should().BeFalse();
        number.Should().Be(0);
    }

    [Fact]
    public void TryParseDigits_TooLongToFitInInt_ReturnsFalseInsteadOfThrowing()
    {
        var act = () => DigitParsingHelper.TryParseDigits(new string('9', 50), out var number);
        act.Should().NotThrow();
        DigitParsingHelper.TryParseDigits(new string('9', 50), out var result).Should().BeFalse();
        result.Should().Be(0);
    }

    [Theory]
    [InlineData("Season 01", 1)]
    [InlineData("Episode 12", 12)]
    [InlineData("今際の国のアリス（１４）", 14)]   // fullwidth digits -- the actual reported crash
    [InlineData("今際の国のアリス（１５）", 15)]
    [InlineData("Chapter ０７", 7)]                 // fullwidth digit, single run
    public void TryParseLeadingNumber_FindsAndParsesFirstDigitRun(string text, int expected)
    {
        DigitParsingHelper.TryParseLeadingNumber(text, out var number).Should().BeTrue();
        number.Should().Be(expected);
    }

    [Fact]
    public void TryParseLeadingNumber_NoDigits_ReturnsFalse()
    {
        DigitParsingHelper.TryParseLeadingNumber("No Number Here", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseLeadingNumber_NeverThrows_EvenOnPathologicalInput()
    {
        var act = () => DigitParsingHelper.TryParseLeadingNumber(new string('9', 50), out _);
        act.Should().NotThrow();
    }
}
