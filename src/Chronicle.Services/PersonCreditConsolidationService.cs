using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Repairs people records using what the credits themselves prove, in two moves: split a stranger off a record, then merge the
/// records that are one person.
///
/// Why this exists. Providers identify people with their own ids, so Chronicle only joins two person records when something proves
/// they are one person, and a record created before that proof arrives stays separate. Live (2026-11-19): Back to the Future Part II
/// listed Michael J. Fox twice (one record from TMDB, one from IMDb) and Thomas F. Wilson twice (the IMDb one is "Tom Wilson"). The
/// existing duplicate review could not merge them because the IMDb record had also picked up a DIFFERENT real person with the same
/// name through a name match (a sound editor's TMDB id, with a different birth date), so the two records' dates conflicted and the
/// pair was dismissed for good.
///
/// Evidence. The same rule the cast list uses (<see cref="CastCollapser.SameOnTitle"/>): on one title, two records are one person
/// when they carry the same name and either credit the same character or come from different providers, or share a surname, a
/// character and no provider. Records linked this way form a component. An id is CONFIRMED when it sits on a credit that took part
/// in such a link.
///
/// Split. When a component holds two different ids from one authoritative provider (two TMDB ids) and one of them is confirmed
/// while another is not, the unconfirmed id belongs to a different person who shares the name. Its credits, its id, its provider
/// data and its photos move onto a new record of its own, so the real person's record is clean. When no id of a source is
/// confirmed, or more than one is (TMDB sometimes holds one actor under two ids, both credited on the same title), nothing is
/// split: the first is unprovable, the second is one person.
///
/// Merge. What remains of a component is merged through <see cref="IMergeService"/> (so each merge is recorded and can be
/// undone), unless two of the records' birth dates, read after the split, still disagree. A component of more than
/// <see cref="MaxComponent"/> records, or one with an unprovable id conflict, is reported and left alone.
///
/// Dry run: <see cref="RunAsync"/> with apply=false reads everything and writes nothing, and returns what it would do.
/// </summary>
public sealed class PersonCreditConsolidationService(
    IServiceScopeFactory scopeFactory,
    ILogger<PersonCreditConsolidationService> logger)
{
    internal const int MaxComponent = 8;
    private const int TitlePage = 400;
    private static readonly Regex TrailingDigits = new(@"(\d+)$", RegexOptions.Compiled);

    public sealed record Finding(string Action, IReadOnlyList<int> PersonIds, string Name, string Detail);

    public sealed class Report
    {
        public bool Applied { get; init; }
        public int TitlesScanned { get; set; }
        public int Components { get; set; }
        public int Merged { get; set; }
        public int Split { get; set; }
        public int Left { get; set; }
        public List<Finding> Findings { get; } = [];
    }

    public Task ExecuteAsync(CancellationToken ct) => RunAsync(apply: true, ct);

    private sealed record Row(int TitleId, int PersonId, string Name, string Source, string? ExtId, string Role, string? Character);

    private sealed class Group
    {
        public int PersonId;
        public string Name = "";
        public List<Row> Rows = [];
        public HashSet<string> Sources = [];
        public HashSet<string> Characters = [];
        public HashSet<string> Roles = [];
    }

    public async Task<Report> RunAsync(bool apply, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var report = new Report { Applied = apply };

        var peopleTypeId = await db.MediaTypes.Where(t => t.IsPersonType).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
        if (peopleTypeId is null) return report;

        // ── 1. Evidence: which person records does a title prove to be one person? ────────────────────────────────────
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; }
            while (p != x) { parent[x] = parent[p]; x = p; p = parent[x]; }
            return x;
        }
        void Union(int a, int b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb); }
        var confirmed = new Dictionary<int, HashSet<string>>();   // person -> "source|bareId" keys that sat on a linked credit

        var titleIds = await db.MediaCredits.AsNoTracking().Where(c => c.PersonMediaItemId != null)
            .Select(c => c.MediaItemId).Distinct().OrderBy(i => i).ToListAsync(ct);

        foreach (var page in titleIds.Chunk(TitlePage))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await (from c in db.MediaCredits.AsNoTracking()
                              join p in db.MediaItems.AsNoTracking() on c.PersonMediaItemId equals p.Id
                              where page.Contains(c.MediaItemId) && p.MediaTypeId == peopleTypeId
                              select new Row(c.MediaItemId, p.Id, p.Name, (c.Source ?? "").ToLower(), c.ExternalPersonId, c.Role, c.CharacterName))
                .ToListAsync(ct);
            report.TitlesScanned += page.Length;

            foreach (var title in rows.GroupBy(r => r.TitleId))
            {
                var groups = title.GroupBy(r => r.PersonId).Select(g => new Group
                {
                    PersonId = g.Key, Name = g.First().Name, Rows = g.ToList(),
                    Sources = g.Select(r => r.Source).ToHashSet(),
                    Characters = CastCollapser.NormalizedCharacters(g.Select(r => (r.Role, r.Character))),
                    Roles = g.Select(r => r.Role.ToLowerInvariant()).ToHashSet(),
                }).ToList();
                if (groups.Count < 2) continue;

                // Only people who could be the same share a surname, so compare within a surname, not every pair on the title.
                foreach (var bucket in groups.GroupBy(g => Surname(g.Name)).Where(b => b.Key.Length > 1 && b.Count() > 1))
                {
                    var list = bucket.ToList();
                    for (var i = 0; i < list.Count; i++)
                        for (var j = i + 1; j < list.Count; j++)
                        {
                            var a = list[i]; var b = list[j];
                            // Same job on the title: a producer credit and a story credit under one name are not evidence of one
                            // person (live dry run: two Anthony Russos on one film).
                            var shared = a.Roles.Intersect(b.Roles).ToHashSet();
                            if (shared.Count == 0) continue;
                            var srcA = a.Rows.Where(r => shared.Contains(r.Role.ToLowerInvariant())).Select(r => r.Source).ToHashSet();
                            var srcB = b.Rows.Where(r => shared.Contains(r.Role.ToLowerInvariant())).Select(r => r.Source).ToHashSet();
                            if (!CastCollapser.SameOnTitle(a.Name, a.Characters, srcA, b.Name, b.Characters, srcB)) continue;
                            Union(a.PersonId, b.PersonId);
                            foreach (var g in new[] { a, b })
                            {
                                if (!confirmed.TryGetValue(g.PersonId, out var set)) confirmed[g.PersonId] = set = [];
                                foreach (var r in g.Rows.Where(r => !string.IsNullOrWhiteSpace(r.ExtId) && shared.Contains(r.Role.ToLowerInvariant())))
                                    set.Add(Key(r.Source, r.ExtId!));
                            }
                        }
                }
            }
        }

        var components = parent.Keys.GroupBy(Find).Select(g => g.OrderBy(x => x).ToList()).Where(g => g.Count > 1)
            .OrderBy(g => g[0]).ToList();
        report.Components = components.Count;

        // ── 2. Per component: split the stranger, merge the rest ────────────────────────────────────────────────────────
        var resolution = scope.ServiceProvider.GetRequiredService<IMetadataResolutionService>();
        var mergeService = scope.ServiceProvider.GetRequiredService<IMergeService>();

        foreach (var ids in components)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await HandleComponentAsync(db, resolution, mergeService, ids, confirmed, apply, report, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                report.Left++;
                report.Findings.Add(new Finding("failed", ids, "", ex.Message));
                logger.LogWarning(ex, "Person consolidation: component {Ids} failed, left unchanged", string.Join(",", ids));
            }
            db.ChangeTracker.Clear();
        }

        logger.LogInformation(
            "Person consolidation ({Mode}): {Titles} titles scanned, {Components} groups of records proven to be one person -- " +
            "{Split} strangers split off, {Merged} records merged, {Left} groups left alone",
            apply ? "applied" : "dry run", report.TitlesScanned, report.Components, report.Split, report.Merged, report.Left);
        return report;
    }

    private async Task HandleComponentAsync(
        ChronicleDbContext db, IMetadataResolutionService resolution, IMergeService mergeService,
        List<int> ids, Dictionary<int, HashSet<string>> confirmed, bool apply, Report report, CancellationToken ct)
    {
        var people = await db.MediaItems.Where(m => ids.Contains(m.Id)).OrderBy(m => m.Id).ToListAsync(ct);
        if (people.Count < 2) return;
        var name = people[0].Name;

        if (people.Count > MaxComponent)
        {
            report.Left++;
            report.Findings.Add(new Finding("left", ids, name, $"{people.Count} records proven alike is too many to merge without a person looking"));
            return;
        }

        var extIds = await db.MediaExternalIds.Where(e => ids.Contains(e.MediaItemId)).ToListAsync(ct);
        var backed = ids.SelectMany(i => confirmed.TryGetValue(i, out var s) ? s : []).ToHashSet();

        // Two different ids from one authoritative provider cannot both be this person, unless a title confirms both.
        var splits = new List<(int Host, string Source, string Bare)>();
        foreach (var bySource in extIds.Where(e => !ExternalIdPolicies.IsNameDerived(e.Source)).GroupBy(e => e.Source.ToLowerInvariant()))
        {
            var bare = bySource.Select(e => Bare(e.ExternalId)).Distinct().ToList();
            if (bare.Count < 2) continue;
            var confirmedHere = bare.Where(b => backed.Contains($"{bySource.Key}|{b}")).ToList();
            if (confirmedHere.Count == 0)
            {
                report.Left++;
                report.Findings.Add(new Finding("left", ids, name,
                    $"two different {bySource.Key} ids ({string.Join(", ", bare)}) and no title confirms either"));
                return;
            }
            // The unconfirmed id is the stranger only when it is clearly the lesser one: fewer credits than the confirmed id(s)
            // together. A post-production executive with five credits beside an actor with one is not "the stranger" (live dry
            // run: Ed Jones, Michael Cassidy); which one is the person cannot be told from the data, so a person decides.
            var counts = await CreditCountsAsync(db, ids, bySource.Key, ct);
            var confirmedCredits = confirmedHere.Sum(b => counts.GetValueOrDefault(b));
            var strangers = bare.Except(confirmedHere).ToList();
            if (strangers.Any(b => counts.GetValueOrDefault(b) >= confirmedCredits))
            {
                report.Left++;
                report.Findings.Add(new Finding("left", ids, name,
                    $"two different {bySource.Key} ids ({string.Join(", ", bare)}): the unconfirmed one has as many credits as the confirmed one, so it cannot be told which is the person"));
                return;
            }
            foreach (var stranger in strangers)
                foreach (var row in bySource.Where(e => Bare(e.ExternalId) == stranger))
                    splits.Add((row.MediaItemId, bySource.Key, stranger));
        }
        splits = splits.Distinct().ToList();

        foreach (var (host, source, bare) in splits)
        {
            report.Split++;
            report.Findings.Add(new Finding("split", [host], people.First(p => p.Id == host).Name,
                $"{source} id {bare} is a different person with the same name (no title confirms it): its credits, id and data move to a new record"));
            if (apply) await SplitStrangerAsync(db, resolution, host, source, bare, ct);
        }

        if (apply && splits.Count > 0)
        {
            db.ChangeTracker.Clear();
            people = await db.MediaItems.Where(m => ids.Contains(m.Id)).OrderBy(m => m.Id).ToListAsync(ct);
        }

        // Dates, read after the split (a stranger's birth date was the usual reason two records seemed to disagree).
        var births = people.Where(p => p.BirthDate.HasValue).Select(p => p.BirthDate!.Value).ToList();
        if (births.Count > 1 && (births.Max() - births.Min()).TotalDays > 366 && (apply || splits.Count == 0))
        {
            report.Left++;
            report.Findings.Add(new Finding("left", ids, name,
                $"birth dates still disagree ({string.Join(" / ", births.Select(b => b.ToString("yyyy-MM-dd")))})"));
            return;
        }
        var deaths = people.Where(p => p.DeathDate.HasValue).Select(p => p.DeathDate!.Value).ToList();
        if (deaths.Count > 1 && (deaths.Max() - deaths.Min()).TotalDays > 366)
        {
            report.Left++;
            report.Findings.Add(new Finding("left", ids, name, "death dates disagree"));
            return;
        }

        // Richest record survives (most ids, then most credits, then oldest).
        var credits = await db.MediaCredits.Where(c => c.PersonMediaItemId != null && ids.Contains(c.PersonMediaItemId.Value))
            .GroupBy(c => c.PersonMediaItemId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var extCount = extIds.GroupBy(e => e.MediaItemId).ToDictionary(g => g.Key, g => g.Count());
        var winner = people.OrderByDescending(p => extCount.GetValueOrDefault(p.Id))
            .ThenByDescending(p => credits.GetValueOrDefault(p.Id)).ThenBy(p => p.Id).First();

        foreach (var loser in people.Where(p => p.Id != winner.Id))
        {
            report.Findings.Add(new Finding("merge", [winner.Id, loser.Id], winner.Name,
                $"a title shows record {loser.Id} and record {winner.Id} are one person; {loser.Id} merges into {winner.Id}"));
            if (!apply) { report.Merged++; continue; }
            try
            {
                await mergeService.MergeAsync(winner.Id, loser.Id, mergedByUserId: null, ct);
                report.Merged++;
                var a = Math.Min(winner.Id, loser.Id); var b = Math.Max(winner.Id, loser.Id);
                var stale = await db.MediaItemDuplicateDismissals.Where(d => d.ItemAId == a && d.ItemBId == b).ToListAsync(ct);
                if (stale.Count > 0) { db.MediaItemDuplicateDismissals.RemoveRange(stale); await db.SaveChangesAsync(ct); }
            }
            catch (InvalidOperationException ex)
            {
                report.Left++;
                report.Findings.Add(new Finding("left", [winner.Id, loser.Id], winner.Name, $"merge refused: {ex.Message}"));
            }
        }
    }

    private static async Task<Dictionary<string, int>> CreditCountsAsync(
        ChronicleDbContext db, List<int> personIds, string source, CancellationToken ct)
    {
        var rows = await db.MediaCredits.AsNoTracking()
            .Where(c => c.PersonMediaItemId != null && personIds.Contains(c.PersonMediaItemId.Value) && c.ExternalPersonId != null)
            .Select(c => new { c.Source, c.ExternalPersonId })
            .ToListAsync(ct);
        return rows.Where(r => string.Equals(r.Source, source, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => Bare(r.ExternalPersonId!)).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>Moves one id and everything that came with it off <paramref name="hostId"/> onto a new record.</summary>
    internal async Task SplitStrangerAsync(
        ChronicleDbContext db, IMetadataResolutionService resolution, int hostId, string source, string bareId, CancellationToken ct)
    {
        var host = await db.MediaItems.FirstAsync(m => m.Id == hostId, ct);
        var extRows = (await db.MediaExternalIds.Where(e => e.MediaItemId == hostId && e.Source == source).ToListAsync(ct))
            .Where(e => Bare(e.ExternalId) == bareId).ToList();
        var credits = (await db.MediaCredits.Where(c => c.PersonMediaItemId == hostId && c.Source == source).ToListAsync(ct))
            .Where(c => c.ExternalPersonId != null && Bare(c.ExternalPersonId) == bareId).ToList();
        if (extRows.Count == 0 && credits.Count == 0) return;

        var stubName = credits.Select(c => c.PersonName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? host.Name;
        var stub = new MediaItem
        {
            MediaTypeId = host.MediaTypeId, Name = stubName, NormalizedName = MediaItemNormalizer.NormalizeName(stubName),
            HierarchyLevel = 0, IsStub = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(stub);

        // The provider's own data about the stranger (its partition, enrichment row and photos) goes with them.
        var root = JsonNode.Parse(host.MetadataJson ?? "{}") as JsonObject ?? new JsonObject();
        var stubRoot = new JsonObject();
        foreach (var key in root.Select(kv => kv.Key).Where(k => !k.StartsWith('_')).ToList())
        {
            if (root[key] is not JsonObject part || PluginIdHelper.ToSource(key) != source) continue;
            var partId = part["externalId"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (partId is null || Bare(partId) != bareId) continue;
            stubRoot[key] = part.DeepClone();
            root.Remove(key);
            var enrichment = await db.MediaEnrichments.FirstOrDefaultAsync(e => e.MediaItemId == hostId && e.PluginId == key, ct);
            if (enrichment is not null)
            {
                enrichment.MediaItem = stub;
                db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = hostId, PluginId = key, Status = EnrichmentStatus.Pending, MaxRetries = 3 });
            }
        }
        stub.MetadataJson = stubRoot.ToJsonString();
        host.MetadataJson = root.ToJsonString();
        host.UpdatedAt = DateTime.UtcNow;

        foreach (var e in extRows) e.MediaItem = stub;
        foreach (var c in credits) c.PersonMediaItem = stub;
        foreach (var h in await db.PersonHeadshots.Where(h => h.PersonMediaItemId == hostId && h.Source == source).ToListAsync(ct))
            h.PersonMediaItem = stub;

        await db.SaveChangesAsync(ct);

        await db.Entry(host).Reference(p => p.MediaType).LoadAsync(ct);
        await resolution.ResolveAsync(host, db, ct);
        await db.Entry(stub).Reference(p => p.MediaType).LoadAsync(ct);
        await resolution.ResolveAsync(stub, db, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Person consolidation: person {HostId} \"{Name}\" held {Source} id {Id}, a different person; moved onto new record {StubId} with {Credits} credit(s)",
            hostId, host.Name, source, bareId, stub.Id, credits.Count);
    }

    private static string Surname(string name) =>
        MediaItemNormalizer.NormalizeName(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    private static string Bare(string id)
    {
        var m = TrailingDigits.Match(id);
        return m.Success ? m.Value : id;
    }

    private static string Key(string source, string id) => $"{source.ToLowerInvariant()}|{Bare(id)}";
}
