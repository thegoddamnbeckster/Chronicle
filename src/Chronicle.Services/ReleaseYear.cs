namespace Chronicle.Services;

/// <summary>
/// What counts as a real release year. Kodi stores an unknown year as -1, which its JSON-RPC reports as
/// 65535; a device search carrying that value minted a new movie with Year 65535 (confirmed live
/// 2026-09-26: "Captain America: The Winter Soldier - Defrosted Edition"), which the device then showed
/// as year 65535 with a premiered date of 65535-11-30. An implausible year is treated as no year.
/// </summary>
public static class ReleaseYear
{
    /// <summary>The first motion picture is from 1888; allow a little headroom.</summary>
    public const int Earliest = 1878;

    /// <summary>Announced-but-unreleased titles carry future years; a decade is generous.</summary>
    public static int Latest => DateTime.UtcNow.Year + 10;

    public static bool IsPlausible(int? year) => year is { } y && y >= Earliest && y <= Latest;

    public static int? OrNull(int? year) => IsPlausible(year) ? year : null;

    /// <summary>
    /// Earliest birth year taken as real. A provider can carry a mistyped date (Hardcover lists a book as available
    /// "June 21, 20222"), and a person can only be born in the past and die by today.
    /// </summary>
    public const int EarliestLifeYear = 1000;

    public static bool IsPlausibleBirthDate(DateTime date) => date.Year >= EarliestLifeYear && date.Date <= DateTime.UtcNow.Date;

    public static bool IsPlausibleDeathDate(DateTime date) => date.Year >= EarliestLifeYear && date.Date <= DateTime.UtcNow.Date.AddDays(1);

    /// <summary>A moment something happened (a watch, a completion): not before the Unix epoch, and not in the future.</summary>
    public static bool IsPlausibleEventTime(DateTime utc) => utc.Year >= 1970 && utc <= DateTime.UtcNow.AddDays(1);

    /// <summary>
    /// A release/air/premiere date string kept only if it parses to a date in the plausible release range, else null. Callers
    /// that pick the first non-empty value across providers therefore fall through to the next provider by precedence.
    /// </summary>
    public static string? OrNullDate(string? date) =>
        !string.IsNullOrWhiteSpace(date)
        && DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
        && IsPlausible(d.Year)
            ? date : null;

    private static readonly System.Text.RegularExpressions.Regex YearFieldRe =
        new(@"""year""\s*:\s*(-?\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A stored metadata document with every impossible "year" (in a plugin's blob or the resolved one) set to null, or
    /// null when nothing needed changing. Plugin blobs written before years were validated keep their bad value otherwise.
    /// </summary>
    public static string? ScrubMetadataJson(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        var bad = false;
        foreach (System.Text.RegularExpressions.Match m in YearFieldRe.Matches(metadataJson))
            if (long.TryParse(m.Groups[1].Value, out var y) && (y > int.MaxValue || !IsPlausible((int)y))) { bad = true; break; }
        if (!bad) return null;

        var root = System.Text.Json.Nodes.JsonNode.Parse(metadataJson) as System.Text.Json.Nodes.JsonObject;
        if (root is null) return null;
        var changed = false;
        foreach (var (_, blob) in root)
        {
            if (blob is not System.Text.Json.Nodes.JsonObject o || !o.TryGetPropertyValue("year", out var yv) || yv is null) continue;
            if (yv.GetValueKind() == System.Text.Json.JsonValueKind.Number && !IsPlausible(yv.GetValue<int?>()))
            {
                o["year"] = null;
                changed = true;
            }
        }
        return changed ? root.ToJsonString() : null;
    }
}
