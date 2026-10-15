using Chronicle.Core.Models;

namespace Chronicle.Services;

/// <summary>
/// Keeps <see cref="MediaItem.Number"/> (the floored ordinal every generic sort / matcher uses) and
/// <see cref="MediaItem.SeriesPosition"/> (the precise, possibly fractional value the UI shows) in step
/// wherever a repair pass gives a book-series item a position -- the same "don't let a stale precise
/// value survive silently" rule BookSeriesService applies on add/remove/reparent.
/// </summary>
internal static class SeriesPositionHelper
{
    internal static int FloorOf(double position) => (int)Math.Floor(position);

    /// <summary>The value books are ordered/matched by: the precise position when known, else Number.</summary>
    internal static double? Effective(MediaItem book) => book.SeriesPosition ?? book.Number;

    /// <summary>Overwrites both fields from <paramref name="position"/> (null clears both).</summary>
    internal static void Set(MediaItem book, double? position)
    {
        book.Number         = position.HasValue ? FloorOf(position.Value) : null;
        book.SeriesPosition = position;
    }

    /// <summary>
    /// A book's position when its title is exactly the series name plus a number ("Mark of the Fool 5" in the
    /// series "Mark of the Fool"), else null. Deliberately strict -- a title that merely ends in digits
    /// ("Fahrenheit 451") or carries a subtitle is never read as a position.
    /// </summary>
    internal static double? InferFromTitle(string? title, string? seriesName)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(seriesName)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            title.Trim(),
            @"^" + System.Text.RegularExpressions.Regex.Escape(seriesName.Trim()) + @"\s+(\d+(?:\.\d+)?)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var position) ? position : null;
    }

    /// <summary>
    /// Fills an EMPTY Number (never overwriting one a provider or the user set), together with the
    /// matching SeriesPosition. A precise SeriesPosition the book already carries wins over
    /// <paramref name="position"/> -- it is the more trusted value, and Number is simply derived from it.
    /// Returns whether anything was written.
    /// </summary>
    internal static bool FillIfEmpty(MediaItem book, double? position)
    {
        if (book.Number.HasValue) return false;
        var precise = book.SeriesPosition ?? position;
        if (!precise.HasValue) return false;
        Set(book, precise);
        return true;
    }
}
