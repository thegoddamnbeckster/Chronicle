using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

/// <summary>
/// A person's credits as the person page lists them: one card per work. A provider that credits at episode level (IMDb lists
/// an actor against each episode they were in) would otherwise bury a show under dozens of individual episode cards, so an
/// episode (or season) of a show-like type is shown as its show. Movies, albums, books and everything else are left as they
/// are, and the character names a person played across one show's episodes are merged onto that show's one card.
/// </summary>
public static class PersonCreditRollup
{
    public sealed record Row(string Role, int MediaItemId, string Name, string? PosterUrl, int? Year, string MediaTypeName, string? CharacterName);

    public static async Task<List<Row>> RollUpAsync(ChronicleDbContext db, IEnumerable<MediaCredit> credits, CancellationToken ct = default)
    {
        var known = new Dictionary<int, MediaItem>();
        foreach (var c in credits)
            if (c.MediaItem is { } m) known[m.Id] = m;

        // An episode or season climbs to its show. The parents are loaded a level at a time with one query per level
        // (an episode's season, then the season's show), not one query per credit.
        static bool Climbs(MediaItem m) =>
            m.HierarchyLevel > 0 && m.ParentId != null && MediaTypeRegistry.IsShowLike(m.MediaType?.Name);

        var frontier = known.Values.Where(Climbs).Select(m => m.ParentId!.Value).Where(id => !known.ContainsKey(id)).Distinct().ToList();
        for (var depth = 0; depth < 5 && frontier.Count > 0; depth++)
        {
            var loaded = new List<MediaItem>();
            foreach (var chunk in frontier.Chunk(400))
                loaded.AddRange(await db.MediaItems.AsNoTracking().Include(m => m.MediaType)
                    .Where(m => chunk.Contains(m.Id)).ToListAsync(ct));
            foreach (var m in loaded) known[m.Id] = m;
            frontier = loaded.Where(Climbs).Select(m => m.ParentId!.Value).Where(id => !known.ContainsKey(id)).Distinct().ToList();
        }

        MediaItem RootOf(MediaItem item)
        {
            var seen = new HashSet<int>();
            while (Climbs(item) && seen.Add(item.Id) && known.TryGetValue(item.ParentId!.Value, out var parent))
                item = parent;
            return item;
        }

        var resolved = new List<(string Role, MediaItem Work, string? Character)>();
        foreach (var c in credits)
        {
            if (c.MediaItem is null) continue;
            // "Cast" and "Actor" are the same role spelled two ways across sources.
            resolved.Add((c.Role == "Cast" ? "Actor" : c.Role, RootOf(c.MediaItem), c.CharacterName));
        }

        return resolved
            .GroupBy(r => (r.Role, r.Work.Id))
            .Select(g =>
            {
                var work = g.First().Work;
                return new Row(g.Key.Role, work.Id, work.Name, work.PosterUrl, work.Year, work.MediaType?.Name ?? string.Empty,
                    MergeCharacters(g.Select(r => r.Character)));
            })
            .ToList();
    }

    /// <summary>
    /// The characters one person played in one work, from however many sources and episodes named them, as one line. Several
    /// sources spell the same part differently ("Ra's al Ghul (voice)" / "Ra's al Ghul", "Captain Gabriel Lorca" /
    /// "USS Discovery Captain Gabriel Lorca", "Lord Felton" / "Felton"), so a name that is only a shorter form of another
    /// one (ignoring a trailing note in brackets) is dropped, keeping the fuller one; distinct parts are joined with " / ".
    /// </summary>
    public static string? MergeCharacters(IEnumerable<string?> names)
    {
        // each credited line may itself already list parts ("James Hook / George Darling")
        var parts = names.Where(n => !string.IsNullOrWhiteSpace(n))
            .SelectMany(n => n!.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(p => (Raw: p, Key: System.Text.RegularExpressions.Regex.Replace(p, @"\s*\([^)]*\)\s*$", "").Trim().ToLowerInvariant()))
            .Where(p => p.Key.Length > 0)
            .ToList();

        var kept = new List<(string Raw, string Key)>();
        foreach (var p in parts.OrderByDescending(p => p.Key.Length).ThenByDescending(p => p.Raw.Length))
        {
            // covered only as WHOLE WORDS ("Captain Gabriel Lorca" inside "USS Discovery Captain Gabriel Lorca"): a plain substring
            // would drop "Dan" next to "Jordan" and "Ann" next to "Hannah", which are different parts
            var covered = kept.FindIndex(k => (" " + k.Key + " ").Contains(" " + p.Key + " ", StringComparison.Ordinal));
            if (covered < 0) kept.Add(p);
        }
        // keep the order the names first appeared in
        var ordered = kept.OrderBy(k => parts.FindIndex(p => p.Raw == k.Raw)).Select(k => k.Raw).ToList();
        return ordered.Count == 0 ? null : string.Join(" / ", ordered);
    }
}
