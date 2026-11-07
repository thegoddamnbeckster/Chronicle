using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

/// <summary>
/// A person's credits as the person page lists them: one card per WORK, and what a work is depends on the kind of media:
///   - a show (TV, anime): the show. A provider that credits at episode level (IMDb lists an actor against each episode) would bury
///     it under dozens of episode cards, so an episode or season is shown as its show;
///   - music: the album. A band is credited on every track, so a track is shown as its album, not as 190 track cards;
///   - everything else (movies, games, books, audiobooks, fan edits ...): the item credited, as it is. A book is the work, not its
///     series or author; a game or film in a collection is itself the work.
/// Character names a person played across one show's episodes are merged onto that show's one card, and the role is named for the
/// kind of media (see <see cref="RoleLabel"/>): cast are "Actor" only where they act.
/// </summary>
public static class PersonCreditRollup
{
    public sealed record Row(string Role, int MediaItemId, string Name, string? PosterUrl, int? Year, string MediaTypeName, string? CharacterName);

    /// <summary>The hierarchy level at which one card stands for a work, or null when the item is itself the work.</summary>
    internal static int? WorkLevel(MediaItem m)
    {
        var name = m.MediaType?.Name;
        if (MediaTypeRegistry.IsShowLike(name)) return 0;
        if (string.Equals(MediaTypeRegistry.Family(name), ProviderFamilies.Music, StringComparison.OrdinalIgnoreCase)
            && (m.MediaType?.HierarchyLevels ?? 1) >= 3)
            return 1;   // artist / album / track: the album (a flat music type, such as music videos, is its own work)
        return null;
    }

    /// <summary>An item below its work's level shows as that work.</summary>
    internal static bool Climbs(MediaItem m) => m.ParentId != null && WorkLevel(m) is { } work && m.HierarchyLevel > work;

    /// <summary>
    /// The role as the person page names it. "Cast" and "Actor" are the same role spelled two ways across sources, but only where cast
    /// actually act; a band's credited artists are "Artist" (Deezer calls the main artist "Main", MusicBrainz "Cast", so both land on one
    /// card); other kinds of media (games, audiobooks) keep "Cast" instead of calling a studio or a narrator an actor.
    /// </summary>
    internal static string RoleLabel(string role, MediaItem item)
    {
        var name = item.MediaType?.Name;
        var music = string.Equals(MediaTypeRegistry.Family(name), ProviderFamilies.Music, StringComparison.OrdinalIgnoreCase);
        if (role.Equals("Actor", StringComparison.OrdinalIgnoreCase) || role.Equals("Cast", StringComparison.OrdinalIgnoreCase))
            return MediaTypeRegistry.CastAreActors(name) ? "Actor" : music ? "Artist" : role;
        if (music && role.Equals("Main", StringComparison.OrdinalIgnoreCase)) return "Artist";
        return role;
    }

    public static async Task<List<Row>> RollUpAsync(ChronicleDbContext db, IEnumerable<MediaCredit> credits, CancellationToken ct = default)
    {
        var known = new Dictionary<int, MediaItem>();
        foreach (var c in credits)
            if (c.MediaItem is { } m) known[m.Id] = m;

        // An item below its work climbs to it (see WorkLevel). The parents are loaded a level at a time with one query per level
        // (an episode's season, then the season's show), not one query per credit.

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
            resolved.Add((RoleLabel(c.Role, c.MediaItem), RootOf(c.MediaItem), c.CharacterName));
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
