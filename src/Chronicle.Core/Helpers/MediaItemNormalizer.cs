using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chronicle.Core.Helpers;

public static class MediaItemNormalizer
{
    private static readonly Regex _strip =
        new(@"[.\-,':!?()]", RegexOptions.Compiled);
    private static readonly Regex _spaces =
        new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex _trailingParenthetical =
        new(@"\s*\([^)]+\)$", RegexOptions.Compiled);

    /// <summary>
    /// Matches a quoted nickname segment, straight or curly double quotes, e.g. the
    /// `"Mike"` in `Michael "Mike" Smith`. Removed (not just its quote characters) before
    /// <see cref="_strip"/> runs, since stripping only the quotes would leave the nickname
    /// text itself in place ("michael mike smith"), which still wouldn't match the same
    /// person's plain "Michael Smith" credit from another source. Deliberately double-quote
    /// only, not single-quote: a single quote is also a real apostrophe in plenty of actual
    /// names (O'Brien, D'Angelo), so treating `'...'` as a nickname delimiter would strip
    /// real name content instead.
    /// </summary>
    private static readonly Regex _quotedNickname =
        new("\\s*[\"\u201C][^\"\u201D]*[\"\u201D]\\s*", RegexOptions.Compiled);

    /// <summary>
    /// Same purpose as <see cref="_quotedNickname"/>, for a nickname delimited by SINGLE quotes
    /// instead of double -- e.g. the `'Wee Man'` in `Jason 'Wee Man' Acuña`. Confirmed live
    /// (2026-09-15): that exact credit split into two Person rows ("Jason Acuña" and "Jason
    /// 'Wee Man' Acuña", same 1973-05-16 birthdate) because the source that included the
    /// nickname used single quotes, which the double-quote-only regex above never matches.
    ///
    /// Distinguishes a nickname delimiter from a real apostrophe (O'Brien, D'Angelo) by
    /// requiring whitespace -- or start/end of string -- immediately OUTSIDE both quote
    /// characters: a nickname is always its own separated token ("Jason 'Wee Man' Acuña" has a
    /// space before the opening quote and after the closing one), while a real apostrophe sits
    /// directly between two letters with no surrounding whitespace at all ("O'Brien"). `(?<!\S)`
    /// / `(?!\S)` cover both "preceded/followed by whitespace" and "at the very start/end of the
    /// string" in one lookaround each, unlike `\s`, which would refuse to match a nickname sitting
    /// right at either edge (`'Stone Cold' Steve Austin`).
    /// </summary>
    private static readonly Regex _quotedNicknameSingle =
        new(@"(?<!\S)'[^']+'(?!\S)", RegexOptions.Compiled);

    /// <summary>
    /// Folds a Unicode base letter plus its combining diacritic marks down to the bare base
    /// letter -- "Acuña" -> "Acuna", "Björgvin" -> "Bjorgvin". Confirmed live (2026-09-15):
    /// "Alex Acuña" (with the tilde) and "Alex Acuna" (without -- an ASCII-transliterated
    /// credit from a source that doesn't preserve it) are the same real person, same
    /// 1944-12-12 birthdate, but NormalizeName's own FormC pass only reconciles two different
    /// ENCODINGS of the identical visible character (see that method's own doc) -- it never
    /// removes a diacritic that's genuinely absent from one side. FormD decomposes each
    /// accented character into its base letter plus separate combining-mark codepoint(s), which
    /// this then filters out by Unicode category (Mn = "nonspacing mark") -- letters that were
    /// never accented in the first place pass through untouched. Used by NormalizeNameLoose
    /// only, not NormalizeName -- see NormalizeNameLoose's own doc for why a wider strip belongs
    /// in the secondary fallback tier, not the primary indexed column.
    /// </summary>
    private static string RemoveDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Produces a canonical lowercase string for duplicate detection.
    /// Strips common punctuation to nothing, collapses whitespace, trims.
    /// "James S. A. Corey" → "james s a corey"
    /// "James S.A. Corey"  → "james sa corey"
    /// "James S.A.Corey"   → "james sacorey"
    ///
    /// Unicode-normalizes to FormC first: the same visible character can arrive as either a
    /// single precomposed codepoint ("ö" = U+00F6) or a base letter plus a combining mark
    /// ("o" + U+0308) depending on which source produced the string, and plain ToLowerInvariant
    /// does not reconcile the two -- they hash and compare as completely different strings.
    /// Confirmed live (2026-09-03): "Björgvin Arnarson" arrived from two different providers in
    /// the two different forms, so PersonResolutionService's own NormalizedName lookup could
    /// never recognize them as the same person and created a duplicate stub every time.
    ///
    /// Also strips a quoted nickname before the punctuation strip runs, e.g.
    /// `Michael "Mike" Smith` -> "michael smith" -- see <see cref="_quotedNickname"/>. Without
    /// this, a source that includes the nickname and one that doesn't (for the same real
    /// person) normalize to two different strings and PersonResolutionService's name-match
    /// step creates a duplicate Person row instead of recognizing the existing one.
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var noNickname = _quotedNickname.Replace(name.Normalize(NormalizationForm.FormC), " ");
        noNickname = _quotedNicknameSingle.Replace(noNickname, " ");
        var stripped = _strip.Replace(noNickname, string.Empty);
        var collapsed = _spaces.Replace(stripped, " ").Trim().ToLowerInvariant();
        return collapsed;
    }

    /// <summary>
    /// A stricter variant of <see cref="NormalizeName"/> for exactly the case its own doc
    /// comment already flags as unresolved: "James S. A. Corey" and "James S.A. Corey" strip
    /// down to different strings ("james s a corey" vs "james sa corey") because whether a
    /// space happened to sit next to the punctuation survives the strip. Root-caused a real
    /// duplicate (2026-08-31, Hardcover audiobook authors): two MediaItems for the same person,
    /// spaced differently around their initials, matched by NormalizeName as different. This
    /// removes ALL whitespace (not just collapsing runs to one space) so spacing around
    /// initials/abbreviations can no longer be the sole difference between two names. Kept
    /// separate from NormalizeName -- which many existing duplicate-detection call sites
    /// already depend on for its current, less aggressive behavior -- rather than changing it
    /// in place; use this as an additional fallback comparison tier, not a replacement.
    ///
    /// Also folds diacritics (see <see cref="RemoveDiacritics"/>) for the identical reason:
    /// root-caused a real duplicate (2026-09-15) where "Alex Acuña" and "Alex Acuna" -- same
    /// real person, one source's credit missing the tilde entirely -- normalized to different
    /// strings under NormalizeName. Widening NormalizeName itself would need a backfill
    /// migration (it's a stored, indexed column many other call sites already depend on); this
    /// fallback tier is exactly where StripTrailingParenthetical's own doc already says that
    /// kind of wider, riskier strip belongs instead.
    /// </summary>
    public static string NormalizeNameLoose(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var noNickname = _quotedNickname.Replace(name.Normalize(NormalizationForm.FormC), " ");
        noNickname = _quotedNicknameSingle.Replace(noNickname, " ");
        var noDiacritics = RemoveDiacritics(noNickname);
        var stripped = _strip.Replace(noDiacritics, string.Empty);
        return _spaces.Replace(stripped, string.Empty).ToLowerInvariant();
    }

    /// <summary>
    /// Strips a trailing disambiguator parenthetical, e.g. "Dogma (film)" -> "Dogma",
    /// "Chosen (TV series)" -> "Chosen". Returns the input unchanged if there's no trailing
    /// "(...)" to strip. Deliberately generic (any trailing "(...)", not a hardcoded list of
    /// known disambiguator words) rather than provider-specific, since any metadata source
    /// could in principle emit a disambiguated title -- Wikipedia is just the one that
    /// actually did (root-caused 2026-08-30/2026-09-02: "Dogma"/"Dogma (film)" and similar
    /// created duplicate MediaItems instead of matching the existing row). This is the same
    /// technique FileScanService.FindByTitleAsync already used for its own matcher (see
    /// _trailingParenthetical there); kept here too so SyncOrchestrationService.CreateStubAsync
    /// and MediaItemMatcher.FindByTitleYearAsync -- which don't share FileScanService's
    /// private matcher -- get the same protection instead of only the file-scan path having it.
    /// NOT folded into NormalizeName itself: that method's current (less aggressive) behavior
    /// is already depended on by other existing call sites, and NormalizedName is a stored,
    /// indexed column -- widening what it strips would need a backfill migration, not just a
    /// method-body change. Call this BEFORE NormalizeName when you want the extra strip.
    /// </summary>
    public static string StripTrailingParenthetical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        return _trailingParenthetical.Replace(name, string.Empty).Trim();
    }
}
