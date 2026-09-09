using System.Text.RegularExpressions;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Scheduled task that automatically resolves "people"-type duplicate candidates
/// (<see cref="Core.Models.MediaItemDuplicateCandidate"/>, populated by
/// <see cref="DuplicateCandidateScanService"/>) that carry strong enough corroborating
/// evidence to act on without a human -- everything else is left in the Duplicates page
/// exactly as before.
///
/// Root-caused live (2026-09-09): a name collision alone is NOT proof of duplication for a
/// "people" item (PersonResolutionService's own doc already tells this story once -- four
/// unrelated real people all named "Brian Johnson" once got merged this way). But when the
/// SAME name ALSO carries real corroborating evidence, that pair genuinely is safe to
/// resolve automatically, and there is no reason to make a person do it by hand one row at a
/// time -- the exact manual process that caught, then had to walk back and finally properly
/// merge, ~5,600 of these in one sitting is the reason this exists. Two directions:
///   - MERGE (via MergeService, so external ids/headshots/credits genuinely consolidate --
///     see MergeService's own doc for the person-specific gaps that fix closed): a shared
///     external id (normalised -- the same TMDB person can be recorded as "movie:12345" by
///     one write path and "tmdb:12345" by another), a shared headshot photo URL, or a
///     matching birth/death date.
///   - DISMISS (mark definitively not-a-duplicate, same as a human clicking Dismiss): a
///     provably CONFLICTING birth or death date under the same name -- e.g. "Henry Kingi"
///     born 1943 vs. 1970 are two different real people, not one person with a typo.
/// Anything else -- same name, nothing else either way -- is genuinely unverifiable from
/// data alone and is left as an open candidate for a human to look at, exactly as the
/// scanner already leaves it.
/// </summary>
public sealed class DuplicatePersonAutoResolveService(
    IServiceScopeFactory scopeFactory,
    ILogger<DuplicatePersonAutoResolveService> logger) : IScheduledTask
{
    public string TaskId      => "duplicate_person_auto_resolve";
    public string DisplayName => "Duplicate Person Auto-Resolve";
    public string Description => "Automatically merges or dismisses duplicate \"people\" candidates that carry strong corroborating evidence (a shared external id, photo, or birthdate -- or a conflicting one); everything else is left in the Duplicates page for manual review.";
    // 30 minutes after Duplicate Candidate Scan's own 2 AM run, so there's always a freshly
    // populated candidate table to work from.
    public string DefaultCron => "30 2 * * *";

    private static readonly Regex TrailingDigits = new(@"(\d+)$", RegexOptions.Compiled);

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var mergeService = scope.ServiceProvider.GetRequiredService<IMergeService>();

        var peopleTypeId = await db.MediaTypes
            .Where(t => t.Name == "people").Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
        if (peopleTypeId is null)
        {
            logger.LogInformation("Duplicate person auto-resolve: no 'people' media type exists yet, nothing to do");
            return;
        }

        // Snapshot candidate ids up front -- the loop below mutates this table as it goes
        // (MergeService removes the pair it just resolved; a merge earlier in this same run
        // can also make a LATER candidate's item disappear, e.g. a three-way duplicate
        // {A,B,C} arrives as candidate rows (A,B),(A,C),(B,C) and merging A/B away first
        // leaves (B,C) pointing at an already-absorbed id -- handled below by treating a
        // missing item as a stale candidate to discard, not an error).
        var candidateIds = await db.MediaItemDuplicateCandidates.Select(c => c.Id).ToListAsync(ct);

        int merged = 0, dismissed = 0, leftForReview = 0, stale = 0;

        foreach (var candidateId in candidateIds)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = await db.MediaItemDuplicateCandidates.FindAsync([candidateId], ct);
            if (candidate is null) { stale++; continue; } // already resolved earlier in this same run

            var a = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == candidate.ItemAId, ct);
            var b = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == candidate.ItemBId, ct);
            if (a is null || b is null)
            {
                db.MediaItemDuplicateCandidates.Remove(candidate);
                await db.SaveChangesAsync(ct);
                stale++;
                continue;
            }
            if (a.MediaTypeId != peopleTypeId || b.MediaTypeId != peopleTypeId)
            {
                leftForReview++; // out of scope for this task -- movies/tv/etc. candidates untouched
                continue;
            }

            var verdict = await EvaluateAsync(db, a.Id, b.Id, ct);
            switch (verdict)
            {
                case Verdict.Conflicting:
                {
                    var lo = Math.Min(a.Id, b.Id);
                    var hi = Math.Max(a.Id, b.Id);
                    db.MediaItemDuplicateCandidates.Remove(candidate);
                    if (!await db.MediaItemDuplicateDismissals.AnyAsync(d => d.ItemAId == lo && d.ItemBId == hi, ct))
                        db.MediaItemDuplicateDismissals.Add(new Core.Models.MediaItemDuplicateDismissal
                        {
                            ItemAId = lo, ItemBId = hi, DismissedAt = DateTime.UtcNow,
                        });
                    await db.SaveChangesAsync(ct);
                    dismissed++;
                    break;
                }
                case Verdict.Corroborated:
                {
                    // Richer side (more external ids on file) survives -- matches the sort
                    // order a human resolving this by hand would use.
                    var aExtCount = await db.MediaExternalIds.CountAsync(e => e.MediaItemId == a.Id, ct);
                    var bExtCount = await db.MediaExternalIds.CountAsync(e => e.MediaItemId == b.Id, ct);
                    var (winnerId, loserId) = aExtCount >= bExtCount ? (a.Id, b.Id) : (b.Id, a.Id);
                    try
                    {
                        await mergeService.MergeAsync(winnerId, loserId, mergedByUserId: null, ct);
                        merged++;
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Eligibility check failed for some reason not anticipated here (e.g.
                        // a merge landed on one of these ids between the snapshot above and
                        // now) -- leave it as a candidate for a human rather than losing it.
                        logger.LogWarning(ex,
                            "Duplicate person auto-resolve: merge of {WinnerId}/{LoserId} was rejected, leaving as a candidate",
                            winnerId, loserId);
                        leftForReview++;
                    }
                    break;
                }
                default:
                    leftForReview++;
                    break;
            }
        }

        logger.LogInformation(
            "Duplicate person auto-resolve complete: {Merged} merged, {Dismissed} dismissed as different " +
            "people, {LeftForReview} left for manual review, {Stale} already resolved earlier this run",
            merged, dismissed, leftForReview, stale);
    }

    private enum Verdict { Corroborated, Conflicting, Unverified }

    private static async Task<Verdict> EvaluateAsync(ChronicleDbContext db, int idA, int idB, CancellationToken ct)
    {
        var extA = await db.MediaExternalIds.Where(e => e.MediaItemId == idA)
            .Select(e => new { e.Source, e.ExternalId }).ToListAsync(ct);
        var extB = await db.MediaExternalIds.Where(e => e.MediaItemId == idB)
            .Select(e => new { e.Source, e.ExternalId }).ToListAsync(ct);

        // Normalised: the same real external id can be recorded under different string
        // conventions by different write paths (confirmed live -- "movie:12345" vs.
        // "tmdb:12345" for the identical TMDB person). Comparing only the trailing numeric/
        // id portion per source catches that without needing every writer fixed first.
        var bareA = extA.GroupBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => BareId(e.ExternalId)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var bareB = extB.GroupBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => BareId(e.ExternalId)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var agreeingSource = bareA.Keys.Any(src => bareB.TryGetValue(src, out var setB) && bareA[src].Overlaps(setB));

        var headsA = await db.PersonHeadshots.Where(h => h.PersonMediaItemId == idA).Select(h => h.Url).ToListAsync(ct);
        var headsB = await db.PersonHeadshots.Where(h => h.PersonMediaItemId == idB).Select(h => h.Url).ToListAsync(ct);
        var sharedHeadshot = headsA.Intersect(headsB, StringComparer.OrdinalIgnoreCase).Any();

        var datesA = await db.MediaItems.Where(m => m.Id == idA)
            .Select(m => new { m.BirthDate, m.DeathDate }).FirstAsync(ct);
        var datesB = await db.MediaItems.Where(m => m.Id == idB)
            .Select(m => new { m.BirthDate, m.DeathDate }).FirstAsync(ct);
        var (birthA, deathA) = (datesA.BirthDate, datesA.DeathDate);
        var (birthB, deathB) = (datesB.BirthDate, datesB.DeathDate);

        var conflictingBirth = birthA.HasValue && birthB.HasValue && birthA != birthB;
        var conflictingDeath = deathA.HasValue && deathB.HasValue && deathA != deathB;
        if (conflictingBirth || conflictingDeath)
            return Verdict.Conflicting;

        var matchingBirth = birthA.HasValue && birthB.HasValue && birthA == birthB;
        if (agreeingSource || sharedHeadshot || matchingBirth)
            return Verdict.Corroborated;

        return Verdict.Unverified;
    }

    private static string BareId(string value)
    {
        var m = TrailingDigits.Match(value);
        return m.Success ? m.Value : value;
    }
}
