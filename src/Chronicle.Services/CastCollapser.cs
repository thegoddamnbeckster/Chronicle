using Chronicle.Core.Helpers;
using Chronicle.Core.Models;

namespace Chronicle.Services;

/// <summary>One person as a title's cast list shows them: every credit they hold on that title, folded into one entry.</summary>
public sealed record CastEntry(MediaItem Person, IReadOnlyList<string> Roles, int BillingOrder, string? CharacterName);

/// <summary>
/// Builds a title's cast list from its credit rows, one entry per real person.
///
/// Why this is more than "group by person id": providers identify people with their own ids (TMDB's numeric id, IMDb's nm-id, a
/// Wikidata Q-id), and Chronicle only joins two person records when it has proof they are one person. Where it has none, one real
/// actor ends up as two person records, each holding that provider's credit, and a plain group-by lists him twice (live,
/// 2026-11-19: Michael J. Fox twice on Back to the Future Part II, one record from TMDB, one from IMDb).
///
/// Two groups on one title are shown as one person when they carry the same name and either (a) both credit the same character,
/// or (b) no provider appears in both. (b) is the signal that matters: a provider lists a person once per title, so the same name
/// under TMDB on one record and under IMDb on the other is one person seen by two providers. The same name under the SAME provider
/// on both, with different characters, is two real people (two actors called Chris Evans in one film) and stays two. This only
/// changes what is shown; the person records themselves are never merged here.
/// </summary>
public static class CastCollapser
{
    public static List<CastEntry> Collapse(IReadOnlyList<MediaCredit> credits)
    {
        var groups = credits
            .Where(c => c.PersonMediaItem != null && c.PersonMediaItemId != null)
            .GroupBy(c => c.PersonMediaItemId!.Value)
            .Select(g => new Group(g.First().PersonMediaItem!, g.ToList()))
            .OrderBy(g => g.Person.Id)
            .ToList();

        // Fold groups that are provably one person, lowest id first so the outcome does not depend on enumeration order.
        var merged = new List<List<Group>>();
        foreach (var g in groups)
        {
            var home = merged.FirstOrDefault(m => m.Any(other => SamePerson(other, g)));
            if (home is null) merged.Add([g]);
            else home.Add(g);
        }

        return merged
            .Select(Build)
            .OrderBy(e => e.BillingOrder)
            .ThenBy(e => e.Person.Name)
            .ToList();
    }

    private sealed record Group(MediaItem Person, List<MediaCredit> Credits)
    {
        public string LooseName { get; } = MediaItemNormalizer.NormalizeNameLoose(Person.Name);
        public HashSet<string> Sources { get; } = Credits.Select(c => (c.Source ?? "").ToLowerInvariant()).ToHashSet();
        public HashSet<string> Characters { get; } = Credits
            .Where(IsActor).SelectMany(c => Parts(c.CharacterName)).Select(MediaItemNormalizer.NormalizeName).Where(n => n.Length > 0).ToHashSet();
    }

    private static bool IsActor(MediaCredit c) => string.Equals(c.Role, "Actor", StringComparison.OrdinalIgnoreCase);

    private static bool SamePerson(Group a, Group b)
    {
        if (a.LooseName.Length == 0 || b.LooseName.Length == 0) return false;
        if (a.LooseName == b.LooseName)
            return a.Characters.Overlaps(b.Characters) || !a.Sources.Overlaps(b.Sources);

        // A shortened given name ("Thomas F. Wilson" from TMDB, "Tom Wilson" from IMDb): the same surname, the same character, and
        // no provider on both records. The three together are what make it safe; any one alone is not.
        return LastWord(a.Person.Name) == LastWord(b.Person.Name) && LastWord(a.Person.Name).Length > 1
            && a.Characters.Overlaps(b.Characters) && !a.Sources.Overlaps(b.Sources);
    }

    private static string LastWord(string name) =>
        MediaItemNormalizer.NormalizeName(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    private static CastEntry Build(List<Group> members)
    {
        // The record to show: one with a photo, then the best-billed, then the oldest.
        var lead = members
            .OrderByDescending(m => !string.IsNullOrWhiteSpace(m.Person.PosterUrl))
            .ThenBy(m => m.Credits.Min(c => c.BillingOrder ?? int.MaxValue))
            .ThenBy(m => m.Person.Id)
            .First();

        var all = members.SelectMany(m => m.Credits).ToList();

        // A single record keeps its credit's own wording exactly. Several records: the lead's wording first, then any character
        // the others add, from every one of their credits (IMDb lists one row per character, TMDB one row for all of them), and
        // compared part by part because two providers often write the same character alike.
        string? character = FirstCharacter(lead.Credits);
        if (members.Count > 1)
        {
            var parts = Parts(character).ToList();
            var seen = parts.Select(MediaItemNormalizer.NormalizeName).ToHashSet();
            foreach (var m in new[] { lead }.Concat(members.Where(m => m != lead).OrderBy(m => m.Person.Id)))
                foreach (var credit in m.Credits.Where(IsActor).OrderBy(c => c.BillingOrder ?? int.MaxValue).ThenBy(c => c.Id))
                    foreach (var part in Parts(credit.CharacterName))
                        if (seen.Add(MediaItemNormalizer.NormalizeName(part))) parts.Add(part);
            character = parts.Count > 0 ? string.Join(" / ", parts) : null;
        }

        return new CastEntry(
            lead.Person,
            all.Select(c => c.Role).Distinct().OrderBy(r => r).ToList(),
            all.Min(c => c.BillingOrder ?? int.MaxValue),
            character);
    }

    private static string? FirstCharacter(IEnumerable<MediaCredit> credits) =>
        credits.Where(c => IsActor(c) && !string.IsNullOrWhiteSpace(c.CharacterName))
            .OrderBy(c => c.BillingOrder ?? int.MaxValue).ThenBy(c => c.Id)
            .Select(c => c.CharacterName).FirstOrDefault();

    private static IEnumerable<string> Parts(string? character) =>
        (character ?? "").Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
