using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Core.Helpers;
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
/// Which provider is believed is not named here: it is the item's authority, the first provider in the
/// Metadata Assignment precedence for the item's media type that has data on the item. Trigger: a movie
/// item owning two or more distinct movie ids of that provider (a provider says which of its ids can only
/// be a movie, ExternalIdPolicy.FamilyIdPatterns). Two real films can never share one item, so at least
/// one of those ids is foreign. Which one is proven by ATTESTATION:
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
    ILogger<MovieExternalIdRepairService> logger)
{
    private const int PageSize = 500;

    /// <summary>
    /// Pure decision: given every external-id row on one item and the text corpus that attests
    /// ids (partitions JSON + enrichment ExternalIds), returns the rows to detach.
    /// </summary>
    internal static List<MediaExternalId> FindForeignIds(
        IReadOnlyList<MediaExternalId> rows, string authoritySource, string attestationCorpus,
        IReadOnlyDictionary<string, string>? enrichedIdBySource = null)
    {
        bool IsMovieId(MediaExternalId r) =>
            string.Equals(r.Source, authoritySource, StringComparison.OrdinalIgnoreCase)
            && ExternalIdPolicies.FamilyOfId(authoritySource, r.ExternalId) == ProviderFamilies.Movie;

        var movieIds = rows.Where(IsMovieId).Select(r => r.ExternalId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (movieIds.Count < 2) return [];

        var foreign = new List<MediaExternalId>();
        // The trigger is about the authority's *movie* ids, so within its source only those are judged --
        // a collection or show id on the same item is a different kind of id, never foreign.
        var judged = rows.Where(r => !string.Equals(r.Source, authoritySource, StringComparison.OrdinalIgnoreCase) || IsMovieId(r));
        foreach (var group in judged.GroupBy(r => r.Source))
        {
            var distinct = group.Select(r => r.ExternalId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count < 2) continue;

            // The provider's OWN enrichment row for this source is the strongest evidence: it is
            // the id that provider actually resolved this item under. It outranks the loose
            // text corpus, which can also mention a foreign id (confirmed live: a Fanart.tv
            // partition carrying another film's id made the wrong id look attested).
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

    /// <summary>
    /// The second rule: an id row that contradicts the id the item's OWN provider data names for the
    /// same source. Live (2026-09-26): the 1990 "Total Recall" carried the 2012 remake's IMDb and
    /// Simkl ids as its only rows for those sources, while its own TMDB record said tt0100802 and its
    /// own Simkl enrichment said 54420 -- so nothing looked "multiple" and the first rule never fired,
    /// and Kodi was then handed the remake's ids for the 1990 film.
    /// <paramref name="authoritativeIds"/> is source -> the id the item's own provider data gives (the
    /// provider's enrichment id, else the id the authority's record cross-references). Rows of a source with
    /// no authority, the authority's own rows, and sources where a row already matches are left alone.
    /// </summary>
    internal static List<MediaExternalId> FindContradictedIds(
        IReadOnlyList<MediaExternalId> rows, string authoritySource, IReadOnlyDictionary<string, string> authoritativeIds)
    {
        var foreign = new List<MediaExternalId>();
        foreach (var group in rows.Where(r => !string.Equals(r.Source, authoritySource, StringComparison.OrdinalIgnoreCase)).GroupBy(r => r.Source))
        {
            if (!authoritativeIds.TryGetValue(group.Key, out var authority) || string.IsNullOrWhiteSpace(authority))
                continue;
            // Only ids written the same way can be compared: an authority's bare number says nothing about a row that
            // holds another provider's slug. A row of a different shape is not judged.
            if (group.Any(r => Shape(Core(r.ExternalId)) != Shape(Core(authority))))
                continue;
            if (group.Any(r => SameCore(r.ExternalId, authority)))
                continue; // an agreeing row exists; the first rule handles any extras
            foreign.AddRange(group);
        }
        return foreign;
    }

    /// <summary>The ids the authority's partition of an item cross-references (extendedData.ids), keyed by external-id
    /// source. Empty when the partition is missing or has none.</summary>
    internal static Dictionary<string, string> CrossReferences(string? metadataJson, string authorityPluginId)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(metadataJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty(authorityPluginId, out var partition) ||
                !partition.TryGetProperty("extendedData", out var ext) || ext.ValueKind != JsonValueKind.Object ||
                !ext.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var prop in ids.EnumerateObject())
            {
                var v = prop.Value;
                var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
                if (!string.IsNullOrWhiteSpace(text)) result[prop.Name.ToLowerInvariant()] = text!;
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>The character classes of an id's core token (digits and letters collapsed), to tell ids written alike from ids that are not.</summary>
    private static string Shape(string core) =>
        Regex.Replace(Regex.Replace(Regex.Replace(core, "[0-9]+", "9"), "[A-Za-z]+", "a"), "[^9a]+", "-");

    private static string Core(string id) => id[(id.LastIndexOf(':') + 1)..];

    private static bool SameCore(string a, string b) =>
        Core(a).Length > 0 && string.Equals(Core(a), Core(b), StringComparison.OrdinalIgnoreCase);

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
        var assignments = scope.ServiceProvider.GetRequiredService<AssignmentConfigCache>();

        // Every flat movie-family item: both rules need the item's own provider data.
        var candidates = await db.MediaItems.AsNoTracking()
            .Where(m => m.HierarchyLevel <= 1 && m.MediaType!.HierarchyLevels == 1 && !m.MediaType.IsPersonType &&
                        m.MediaType.ProviderFamily == ProviderFamilies.Movie && m.MetadataJson != null)
            .Select(m => new { m.Id, TypeName = m.MediaType!.Name })
            .ToListAsync(ct);

        // The authority of a media type is the first provider in its Metadata Assignment precedence (the title's list first).
        var precedence = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var typeName in candidates.Select(c => c.TypeName).Distinct())
        {
            var byField = await assignments.GetForTypeAsync(typeName, 0, ct);
            precedence[typeName] = (byField.TryGetValue("title", out var titles) ? titles : [])
                .Concat(byField.Values.SelectMany(v => v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        int repaired = 0, detachedRows = 0;
        foreach (var chunk in candidates.Chunk(PageSize))
        {
            ct.ThrowIfCancellationRequested();
            var chunkIds = chunk.Select(c => c.Id).ToList();
            var typeById = chunk.ToDictionary(c => c.Id, c => c.TypeName);
            var items = await db.MediaItems.Where(m => chunkIds.Contains(m.Id)).ToListAsync(ct);
            var allRows = await db.MediaExternalIds.Where(e => chunkIds.Contains(e.MediaItemId)).ToListAsync(ct);
            var enrichments = await db.MediaEnrichments.AsNoTracking()
                .Where(e => chunkIds.Contains(e.MediaItemId) && e.ExternalId != null)
                .Select(e => new { e.MediaItemId, e.PluginId, e.ExternalId })
                .ToListAsync(ct);

            foreach (var item in items)
            {
                // The authority: the highest-precedence provider that actually has data on this item.
                var authorityPluginId = precedence[typeById[item.Id]]
                    .FirstOrDefault(pid => item.MetadataJson!.Contains("\"" + pid + "\""));
                if (authorityPluginId is null) continue;
                var authoritySource = PluginIdHelper.ToSource(authorityPluginId);

                var rows = allRows.Where(r => r.MediaItemId == item.Id).ToList();
                var corpus = (item.MetadataJson ?? "") + "\n" +
                             string.Join("\n", enrichments.Where(e => e.MediaItemId == item.Id).Select(e => e.ExternalId));

                var enrichedIds = enrichments.Where(e => e.MediaItemId == item.Id)
                    .GroupBy(e => PluginIdHelper.ToSource(e.PluginId))
                    .ToDictionary(g => g.Key, g => g.First().ExternalId!, StringComparer.OrdinalIgnoreCase);
                var foreign = FindForeignIds(rows, authoritySource, corpus, enrichedIds);

                // Second rule: rows contradicting the item's own provider data. The enrichment id
                // wins; the authority record's cross-references fill the sources with no
                // enrichment row of their own.
                var authority = CrossReferences(item.MetadataJson, authorityPluginId);
                foreach (var (source, id) in enrichedIds) authority[source] = id;
                foreach (var row in FindContradictedIds(rows, authoritySource, authority))
                    if (!foreign.Contains(row)) foreign.Add(row);

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
            "Movie external id repair: {Candidates} movie(s) checked -- repaired {Repaired}, detached {Rows} id row(s)",
            candidates.Count, repaired, detachedRows);
    }
}
