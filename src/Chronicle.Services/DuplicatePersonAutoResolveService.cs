using System.Text.RegularExpressions;
using Chronicle.Core.Helpers;
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
///     one write path and "tmdb:12345" by another), a shared headshot photo URL, a matching
///     birth/death date, or both sides crediting the exact same character name on the exact
///     same title (e.g. two stubs both crediting "Roy Kent" on Ted Lasso) -- two different
///     real people sharing a name AND independently cast under the identical character on
///     the identical show is not a realistic coincidence.
///   - DISMISS (mark definitively not-a-duplicate, same as a human clicking Dismiss): a
///     provably CONFLICTING birth or death date under the same name -- e.g. "Henry Kingi"
///     born 1943 vs. 1970 are two different real people, not one person with a typo -- OR a
///     DIFFERENT id from the same authoritative (non-name-search-derived) source on each side
///     (e.g. two different tmdb ids). Root-caused live (2026-09-19): with ~15,800 "people"
///     candidates backlogged and 12,800+ of them "people", a sample of 300 showed 298 fell
///     into exactly this shape -- same normalized name, same source (almost always tmdb)
///     present on both sides, but a DIFFERENT id. That's the identical signal
///     PersonResolutionService's own Step 2/2b guard already trusts to REFUSE attaching a new
///     credit (see its own doc, the "Brian Johnson" incident) -- a different catalog id from
///     the same authoritative provider is overwhelmingly evidence of two different real
///     people sharing a name, not one person recorded twice. Extending that same trusted
///     signal here to auto-dismiss (rather than leaving it open forever) is what actually
///     drains the backlog; leaving 12,800+ rows sitting in an unreviewable queue serves no
///     one. Checked strictly AFTER every corroboration signal above, never before -- a shared
///     character/title or an agreeing id on a DIFFERENT source can still prove genuine
///     identity even when one particular source's own id disagrees (confirmed live: this
///     exact shape happened for the real actor Brett Goldstein, who has two different tmdb
///     person ids on file -- a TMDB data-quality issue, not a Chronicle bug -- but both stubs
///     shared the identical "Roy Kent"/Ted Lasso credit, so the corroboration check above
///     catches it before this dismissal check ever runs).
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

    /// <summary>
    /// Sources whose "people" external id is derived from a name-based search match Chronicle
    /// ran on its own, not a structured id handed over directly by an authoritative payload
    /// (TMDB's numeric person id arrives in a title's own cast list; MusicBrainz/Hardcover ids
    /// come from an exact catalog lookup). An id from a source in this set can be wrong in
    /// exactly the way that also makes two different real people look like the same one --
    /// confirmed live (2026-09-14): Wikipedia's own person search matched the identical wrong
    /// article ("Anthony Edwards (actor)") onto both the actor's stub and an unrelated NBA
    /// player's stub, and this service then merged them because their two "wikipedia" ids
    /// agreed -- they agreed because the SAME upstream search bug produced both, not because
    /// they're actually the same person. Treating that agreement as corroborating evidence is
    /// circular: the id itself is exactly as unreliable as the merge decision it's supposed to
    /// validate. Excluded from <c>agreeingSource</c> below for that reason; still eligible via
    /// a shared headshot or matching birthdate, and still gets attached to items normally --
    /// only its use as auto-merge proof is restricted.
    /// </summary>
    private static readonly HashSet<string> NameSearchDerivedSources =
        new(StringComparer.OrdinalIgnoreCase) { "wikipedia" };

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
        // Sources present (with a real, non-search-derived id) on BOTH sides -- the set this
        // pair can actually be cross-checked against. agreeingSource looks for any of those
        // sources where the ids actually overlap; conflictingSourceId (used below, only after
        // every corroboration check has had its say) looks for one where they don't.
        var commonAuthoritativeSources = bareA.Keys
            .Where(src => !NameSearchDerivedSources.Contains(src) && bareB.ContainsKey(src))
            .ToList();
        var agreeingSource = commonAuthoritativeSources.Any(src => bareA[src].Overlaps(bareB[src]));
        var conflictingSourceId = commonAuthoritativeSources.Any(src => !bareA[src].Overlaps(bareB[src]));

        // Same exclusion as agreeingSource above, and for the same reason: a headshot recorded
        // under a name-search-derived source came from whatever article that search matched,
        // so two stubs sharing one is only proof they hit the SAME search bug, not that they're
        // the same person. The source filter is applied AFTER ToListAsync, in memory -- EF Core
        // translates a `Where` clause to SQL, where equality follows the column's DB collation
        // (case-sensitive by default in SQLite), not NameSearchDerivedSources' own
        // OrdinalIgnoreCase comparer; filtering client-side is what actually makes the exclusion
        // case-insensitive the way agreeingSource's own (already in-memory) check above is.
        var rawHeadsA = await db.PersonHeadshots
            .Where(h => h.PersonMediaItemId == idA)
            .Select(h => new { h.Url, h.Source }).ToListAsync(ct);
        var rawHeadsB = await db.PersonHeadshots
            .Where(h => h.PersonMediaItemId == idB)
            .Select(h => new { h.Url, h.Source }).ToListAsync(ct);
        var headsA = rawHeadsA.Where(h => !NameSearchDerivedSources.Contains(h.Source)).Select(h => h.Url);
        var headsB = rawHeadsB.Where(h => !NameSearchDerivedSources.Contains(h.Source)).Select(h => h.Url);
        var sharedHeadshot = headsA.Intersect(headsB, StringComparer.OrdinalIgnoreCase).Any();

        var datesA = await db.MediaItems.Where(m => m.Id == idA)
            .Select(m => new { m.BirthDate, m.DeathDate }).FirstAsync(ct);
        var datesB = await db.MediaItems.Where(m => m.Id == idB)
            .Select(m => new { m.BirthDate, m.DeathDate }).FirstAsync(ct);
        var (birthA, deathA) = (datesA.BirthDate, datesA.DeathDate);
        var (birthB, deathB) = (datesB.BirthDate, datesB.DeathDate);

        var conflictingBirth = birthA.HasValue && birthB.HasValue && birthA != birthB;
        var conflictingDeath = deathA.HasValue && deathB.HasValue && deathA != deathB;
        var matchingBirth = birthA.HasValue && birthB.HasValue && birthA == birthB;

        // A conflicting birth or death date is checked FIRST, unconditionally, same as before
        // this file's 2026-09-19 same-source-id-conflict addition -- caught in review: an
        // earlier version of that addition moved ALL corroboration checks ahead of ALL conflict
        // checks, which went further than intended and let a merely-matching birthdate override
        // a genuinely conflicting death date (two different real people who happen to share a
        // birthdate but have distinct, correctly-recorded death dates). A conflicting birth/death
        // date has no benign explanation the way a same-source id mismatch can (see
        // conflictingSourceId's own doc below) -- it stays the highest-precedence signal.
        if (conflictingBirth || conflictingDeath)
            return Verdict.Conflicting;

        if (agreeingSource || sharedHeadshot || matchingBirth)
            return Verdict.Corroborated;

        // Both sides crediting the identical character name on the identical title -- see this
        // class's own doc for why this is trusted as corroboration (independent of any id at
        // all): two different real people sharing a name AND independently cast under the same
        // character on the same show is not a realistic coincidence. Only reached when no
        // cheaper signal above already answered it -- avoids two extra queries (and the
        // O(n*m) comparison below) per pair for the common case, a real cost at the confirmed
        // backlog scale (12,800+ "people" candidates in one run).
        var creditsA = await db.MediaCredits.Where(c => c.PersonMediaItemId == idA)
            .Select(c => new { c.MediaItemId, c.Role, c.CharacterName }).ToListAsync(ct);
        var creditsB = await db.MediaCredits.Where(c => c.PersonMediaItemId == idB)
            .Select(c => new { c.MediaItemId, c.Role, c.CharacterName }).ToListAsync(ct);
        // Actor-role credits only -- same restriction as MediaController.GetPeople's own
        // character-name lookup (a crew credit's CharacterName is never populated in practice,
        // but this keeps that explicit rather than accidental, matching that method's own
        // doc). Compared via MediaItemNormalizer.NormalizeName, the same helper this file's
        // own person-name matching already trusts for "same visible name, different
        // formatting" (punctuation/diacritics/whitespace), rather than a raw Trim +
        // OrdinalIgnoreCase that would miss those same variations for a character name.
        static bool IsActorCredit(string role) => string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase);
        var sharedTitleCharacter = creditsA.Any(a =>
            IsActorCredit(a.Role) && !string.IsNullOrWhiteSpace(a.CharacterName) &&
            creditsB.Any(b => b.MediaItemId == a.MediaItemId && IsActorCredit(b.Role) &&
                !string.IsNullOrWhiteSpace(b.CharacterName) &&
                MediaItemNormalizer.NormalizeName(b.CharacterName) == MediaItemNormalizer.NormalizeName(a.CharacterName)));

        if (sharedTitleCharacter)
            return Verdict.Corroborated;

        if (conflictingSourceId)
            return Verdict.Conflicting;

        return Verdict.Unverified;
    }

    private static string BareId(string value)
    {
        var m = TrailingDigits.Match(value);
        return m.Success ? m.Value : value;
    }
}
