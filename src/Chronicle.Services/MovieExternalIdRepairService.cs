using System.Text.RegularExpressions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Scheduled task that strips external ids belonging to a DIFFERENT film off a movie item.
///
/// Root-caused live (2026-09-24): the 1974 "The Longest Yard" item also carried the 2005 remake's
/// TMDB, IMDb, Simkl, TVDB and Fanart ids, so in Add Media the 2005 search result matched the
/// 1974 item by id and both results linked to it. 93 movies had the same shape (Footloose 2011
/// carrying the 1984 id, The Thing, The Abyss ...). The merge log holds no trace of how the extra
/// ids arrived, so this repairs the data rather than assuming one cause.
///
/// Trigger: a movie item owning two or more distinct TMDB "movie:N" ids. Two real films can never
/// share one item, so at least one of those ids is foreign. Which one is proven by ATTESTATION:
/// an id is attested when a provider actually returned it for this item -- it appears in the
/// item's own provider partitions (a partition's externalId/cross-reference ids) or as an
/// enrichment row's ExternalId. Within each source, if at least one id is attested, every
/// unattested id of that source is detached from the item. If NO id of a source is attested the
/// evidence can't tell them apart, so that source is left alone -- never a guess.
///
/// Detach only, no new record: a foreign id detached here simply stops claiming the item, and the
/// other film then shows up correctly as "not in library" and can be added on its own. Nothing
/// else about the item (name, year, files, watch history, partitions) is touched.
/// </summary>
public sealed class MovieExternalIdRepairService(
    IServiceScopeFactory scopeFactory,
    ILogger<MovieExternalIdRepairService> logger) : IScheduledTask
{
    public string TaskId      => "movie_external_id_repair";
    public string DisplayName => "Movie External ID Repair";
    public string Description => "Finds movies that carry another film's external ids (e.g. a remake's TMDB id) and detaches the ids that no provider actually returned for that movie.";
    public string DefaultCron => "15 3 * * *";

    private const int PageSize = 500;

    /// <summary>
    /// Pure decision: given every external-id row on one item and the text corpus that attests
    /// ids (partitions JSON + enrichment ExternalIds), returns the rows to detach.
    /// </summary>
    internal static List<MediaExternalId> FindForeignIds(
        IReadOnlyList<MediaExternalId> rows, string attestationCorpus,
        IReadOnlyDictionary<string, string>? enrichedIdBySource = null)
    {
        var tmdbMovieIds = rows
            .Where(r => r.Source == "tmdb" && r.ExternalId.StartsWith("movie:", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ExternalId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tmdbMovieIds.Count < 2) return [];

        var foreign = new List<MediaExternalId>();
        // The trigger is about TMDB *movie* ids, so within the tmdb source only "movie:" rows are
        // judged -- a collection:/tv: id on the same item is a different kind of id, never foreign.
        var judged = rows.Where(r => r.Source != "tmdb" ||
                                     r.ExternalId.StartsWith("movie:", StringComparison.OrdinalIgnoreCase));
        foreach (var group in judged.GroupBy(r => r.Source))
        {
            var distinct = group.Select(r => r.ExternalId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count < 2) continue;

            // The provider's OWN enrichment row for this source is the strongest evidence: it is
            // the id that provider actually resolved this item under. It outranks the loose
            // text corpus, which can also mention a foreign id (confirmed live: a Fanart.tv
            // partition carrying another film's id made the wrong TMDB id look attested).
            HashSet<string> attested;
            if (enrichedIdBySource is not null && enrichedIdBySource.TryGetValue(group.Key, out var enrichedId) &&
                distinct.Any(id => SameCore(id, enrichedId)))
                attested = distinct.Where(id => SameCore(id, enrichedId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            else
                attested = distinct.Where(id => IsAttested(id, attestationCorpus)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (attested.Count == 0) continue; // can't tell them apart -- leave alone

            foreign.AddRange(group.Where(r => !attested.Contains(r.ExternalId)));
        }
        return foreign;
    }

    private static string Core(string id) => id[(id.LastIndexOf(':') + 1)..];

    private static bool SameCore(string a, string b) =>
        Core(a).Length > 0 && string.Equals(Core(a), Core(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Provider plugin id ("chronicle.plugin.thetvdb") to the external-id Source it writes
    /// ("tvdb"); every other plugin's source is simply the last id segment.</summary>
    internal static string SourceOfPlugin(string pluginId)
    {
        var last = pluginId[(pluginId.LastIndexOf('.') + 1)..];
        return last == "thetvdb" ? "tvdb" : last;
    }

    /// <summary>An id counts as attested when its core token (the part after the last ':' -- "9291"
    /// from "movie:9291", "tt0398165" as-is) appears standalone in the corpus.</summary>
    internal static bool IsAttested(string externalId, string corpus)
    {
        var core = externalId[(externalId.LastIndexOf(':') + 1)..];
        if (core.Length == 0) return false;
        return Regex.IsMatch(corpus, $@"(?<![A-Za-z0-9]){Regex.Escape(core)}(?![A-Za-z0-9])");
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var candidateIds = await db.MediaExternalIds.AsNoTracking()
            .Where(e => e.Source == "tmdb" && e.ExternalId.StartsWith("movie:"))
            .GroupBy(e => e.MediaItemId)
            .Where(g => g.Select(x => x.ExternalId).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(ct);

        int repaired = 0, detachedRows = 0;
        foreach (var chunk in candidateIds.Chunk(PageSize))
        {
            ct.ThrowIfCancellationRequested();
            var items = await db.MediaItems.Where(m => chunk.Contains(m.Id)).ToListAsync(ct);
            var allRows = await db.MediaExternalIds.Where(e => chunk.Contains(e.MediaItemId)).ToListAsync(ct);
            var enrichments = await db.MediaEnrichments.AsNoTracking()
                .Where(e => chunk.Contains(e.MediaItemId) && e.ExternalId != null)
                .Select(e => new { e.MediaItemId, e.PluginId, e.ExternalId })
                .ToListAsync(ct);

            foreach (var item in items)
            {
                var rows = allRows.Where(r => r.MediaItemId == item.Id).ToList();
                var corpus = (item.MetadataJson ?? "") + "\n" +
                             string.Join("\n", enrichments.Where(e => e.MediaItemId == item.Id).Select(e => e.ExternalId));

                var enrichedIds = enrichments.Where(e => e.MediaItemId == item.Id)
                    .GroupBy(e => SourceOfPlugin(e.PluginId))
                    .ToDictionary(g => g.Key, g => g.First().ExternalId!, StringComparer.OrdinalIgnoreCase);
                var foreign = FindForeignIds(rows, corpus, enrichedIds);
                if (foreign.Count == 0) continue;

                db.MediaExternalIds.RemoveRange(foreign);
                repaired++;
                detachedRows += foreign.Count;
                logger.LogInformation(
                    "Movie external id repair: item {ItemId} \"{Name}\" ({Year}) -- detached foreign ids {Ids}",
                    item.Id, item.Name, item.Year, string.Join(", ", foreign.Select(f => $"{f.Source}:{f.ExternalId}")));
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        logger.LogInformation(
            "Movie external id repair: {Candidates} movie(s) with multiple TMDB ids checked -- repaired {Repaired}, detached {Rows} id row(s)",
            candidateIds.Count, repaired, detachedRows);
    }
}
