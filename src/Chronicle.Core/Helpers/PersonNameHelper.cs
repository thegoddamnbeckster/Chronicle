namespace Chronicle.Core.Helpers;

/// <summary>
/// Derives a "last name first" sort key from a person's full display name, for alphabetizing
/// the People catalog by last name without a separate first/last name schema (a person is
/// just a MediaItem with a single Name field). Best-effort, not a full name parser: handles
/// the common cases (a plain surname, a trailing generational suffix, a lowercase particle
/// immediately before the surname) and falls back to the name as-is for anything it can't
/// confidently split (a mononym, or a name that's already a single word).
///
/// Applied identically to both a candidate person's Name and a typed jump-search/A-Z-rail
/// target (PeopleController.GetPeople) so the two compare on the same key space.
/// </summary>
public static class PersonNameHelper
{
    private static readonly HashSet<string> Suffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "jr", "jr.", "sr", "sr.", "ii", "iii", "iv", "v",
    };

    private static readonly HashSet<string> Particles = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "del", "della", "der", "di", "du", "la", "le", "van", "von", "da", "dos", "das",
    };

    public static string ToLastNameFirstSortKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (name ?? string.Empty).Trim();

        var tokens = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 1)
            return name.Trim();

        var end = tokens.Length - 1;
        // A trailing generational suffix isn't the surname -- the real one is the token(s)
        // before it ("Martin Luther King Jr." -> surname "King", not "Jr.").
        if (Suffixes.Contains(tokens[end].TrimEnd('.')))
            end--;
        if (end < 0)
            return name.Trim(); // pathological input (e.g. "Jr." alone) -- just fall back

        var start = end;
        // Pull in any lowercase particle(s) immediately preceding the surname, so "Guillermo
        // del Toro" sorts under "del Toro", not just "Toro".
        while (start > 0 && Particles.Contains(tokens[start - 1]))
            start--;

        var lastName = string.Join(' ', tokens[start..(end + 1)]);
        var restTokens = tokens[..start].Concat(tokens[(end + 1)..]);
        var rest = string.Join(' ', restTokens);
        return rest.Length == 0 ? lastName : $"{lastName} {rest}";
    }
}
