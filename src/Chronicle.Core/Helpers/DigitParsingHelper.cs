using System.Globalization;
using System.Text.RegularExpressions;

namespace Chronicle.Core.Helpers;

/// <summary>
/// Safe integer parsing for digit runs that may include non-ASCII Unicode decimal digits
/// (e.g. fullwidth CJK digits like "１４"). Regex \d matches the whole Unicode Nd category,
/// not just ASCII 0-9, but int.Parse only accepts ASCII digits and throws FormatException on
/// anything else -- confirmed live (2026-08-30) crashing metadata enrichment for a title like
/// "今際の国のアリス（１４）" (Alice in Borderland vol. 14, fullwidth digits). Use these
/// instead of int.Parse whenever the source digits came from user- or provider-supplied text
/// (titles, filenames) rather than a value Chronicle itself generated.
/// </summary>
public static class DigitParsingHelper
{
    /// <summary>
    /// Parses a string already confirmed to be one or more Unicode decimal digits (e.g. a
    /// regex \d+ capture group) into an integer, digit-by-digit via
    /// CharUnicodeInfo.GetDecimalDigitValue -- so fullwidth/other-script digits resolve to
    /// their real numeric value instead of crashing (int.Parse) or being silently dropped.
    /// Returns false (rather than throwing) for an empty string, a non-digit character, or a
    /// digit run long enough to overflow int.
    /// </summary>
    public static bool TryParseDigits(string digits, out int number)
    {
        number = 0;
        if (string.IsNullOrEmpty(digits)) return false;

        // Accumulates into a local, not directly into `number`, so a mid-string failure
        // (a non-digit character, or an overflow) leaves `number` at its default 0 -- the
        // standard TryXxx contract (matching int.TryParse) -- rather than a partial,
        // misleadingly-plausible value from however far accumulation got before failing.
        var accumulated = 0;
        try
        {
            checked
            {
                foreach (var c in digits)
                {
                    var digit = CharUnicodeInfo.GetDecimalDigitValue(c);
                    if (digit < 0) return false; // not actually a decimal digit
                    accumulated = accumulated * 10 + digit;
                }
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        number = accumulated;
        return true;
    }

    /// <summary>
    /// Finds the first run of Unicode decimal digits anywhere in text and parses it via
    /// <see cref="TryParseDigits"/>. Returns false if no digit run is found.
    /// </summary>
    public static bool TryParseLeadingNumber(string text, out int number)
    {
        number = 0;
        var match = Regex.Match(text, @"\d+");
        return match.Success && TryParseDigits(match.Value, out number);
    }
}
