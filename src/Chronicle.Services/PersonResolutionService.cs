using System.Collections.Concurrent;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Resolves a credit (cast/crew entry from a plugin's MediaMetadata, or a legacy Trakt import
/// credit) onto a real "people"-type MediaItem, creating a stub person the first time anyone is
/// credited under that name/external id. See docs/plans/2026-08-28-people-section-design.md
/// Section 3 for the full design; this is a direct implementation of its 7-step algorithm.
///
/// Known limitation, stated plainly (per the design doc): name-only resolution (steps 2-3, when
/// no ExternalPersonId is available) carries a real, accepted common-name collision risk -- two
/// different real people with the same name and no external ID on EITHER credit will still merge
/// into one person item; there's no way to catch that case without an id on at least one side.
/// Mitigated, not eliminated, by preferring ID-based resolution wherever a plugin supplies one,
/// and (see the same-source-conflict check in ResolvePersonOnlyAsync's Step 2) by refusing a
/// name-only match when the credit's own source already has a DIFFERENT id on file for that
/// name -- the confirmed failure mode found live in production (four unrelated real people
/// named "Brian Johnson" collapsed onto one person item this way).
/// </summary>
public class PersonResolutionService(
    IPluginRegistry pluginRegistry,
    IMetadataResolutionService resolutionService,
    ILogger<PersonResolutionService> logger) : IPersonResolutionService
{
    // Per-(peopleTypeId, loose-normalized-name) lock guarding ResolvePersonOnlyAsync's Steps
    // 2/2b/3 -- see the doc comment at that lock's acquisition site for what this prevents
    // (two different plugins concurrently resolving the same person name, each missing the
    // other's uncommitted insert). Same static-ConcurrentDictionary-of-SemaphoreSlim pattern as
    // MetadataEnrichmentService's own per-plugin/per-(item,plugin) locks.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _personNameLocks = new();
    private static readonly TimeSpan NameLockTimeout = TimeSpan.FromSeconds(40);


    public async Task ResolveAndRecordCreditAsync(
        ChronicleDbContext db,
        int titleMediaItemId,
        string personName,
        string? externalPersonId,
        string source,
        string? profileImageUrl,
        string role,
        string? characterName,
        int? billingOrder,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personName))
            return;

        var person = await ResolvePersonOnlyAsync(db, personName, externalPersonId, source, ct);

        // Step 5: accumulate the headshot, if supplied (feed path 2 of Section 1.5 -- a credit
        // on someone else's title).
        if (!string.IsNullOrWhiteSpace(profileImageUrl))
            await RecordHeadshotsIfNewAsync(db, person, [(profileImageUrl, null)], source, ct);

        // Step 6: write the credit row itself -- one row per call; the caller owns clearing any
        // prior rows for (titleMediaItemId, source) before looping, same delete-and-reinsert
        // pattern SyncOrchestrationService.FetchAndStoreCreditsAsync already uses.
        db.MediaCredits.Add(new MediaCredit
        {
            MediaItemId       = titleMediaItemId,
            PersonName        = personName,
            Role              = role,
            CharacterName     = characterName,
            BillingOrder      = billingOrder,
            Source            = source,
            ExternalPersonId  = externalPersonId,
            PersonMediaItemId = person.Id,
        });
    }

    /// <summary>
    /// The same algorithm as <see cref="ResolveAndRecordCreditAsync"/> applied to a whole credit
    /// list, built for speed. Root-caused live (2026-10-02): the IMDb plugin's first full run was
    /// recording 2-5 credits a second, because every credit ran its own lookups and every new
    /// person cost two separate saves (stub, then its enrichment rows), each one a write the
    /// other enrichment workers had to queue behind. Here, people are found with a handful of
    /// set queries and every new stub of the batch is saved in one go.
    ///
    /// Same rules, in the same order, credit by credit: (1) external id; (2) exact normalized
    /// name, then (2b) loose name, each refused when that person already holds a different id
    /// from this source (counting ids assigned earlier in this same batch); (3) otherwise a new
    /// stub; (4) record the id. Name locks: every name this batch might create is locked (in
    /// sorted order, so two batches can't deadlock) BEFORE the name lookups and held until the
    /// new stubs are saved, which keeps the original guarantee that two workers never create
    /// two stubs for one name. As before, a lock that can't be had in time is proceeded without.
    /// One deliberate improvement: a credit whose id another credit in the same batch already
    /// attached goes to that same person, even under a different spelling, instead of a second
    /// stub carrying the same id.
    /// </summary>
    public async Task ResolveAndRecordCreditsAsync(
        ChronicleDbContext db, int titleMediaItemId, IReadOnlyList<CreditToRecord> credits, string source,
        CancellationToken ct = default)
    {
        var list = credits.Where(c => !string.IsNullOrWhiteSpace(c.PersonName)).ToList();
        if (list.Count == 0) return;

        var peopleTypeId = await GetPeopleMediaTypeIdAsync(db, ct);
        static string? Id(CreditToRecord c) => string.IsNullOrWhiteSpace(c.ExternalPersonId) ? null : c.ExternalPersonId;

        // Ids from this source already attached to someone, in the database or earlier in this
        // context (the "pending" half of the original HasConflictingSourceIdAsync check).
        var personByExtId = new Dictionary<string, MediaItem>(StringComparer.Ordinal);
        var extIdsOf = new Dictionary<int, HashSet<string>>();
        void Attach(MediaItem p, string extId)
        {
            personByExtId.TryAdd(extId, p);
            if (!extIdsOf.TryGetValue(p.Id, out var set)) extIdsOf[p.Id] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(extId);
        }

        // Step 1, for the whole list: external ids.
        var extIds = list.Select(Id).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (var chunk in extIds.Chunk(500))
        {
            var rows = await db.MediaExternalIds
                .Where(x => x.Source == source && chunk.Contains(x.ExternalId)
                            && x.MediaItem != null && x.MediaItem.MediaTypeId == peopleTypeId)
                .OrderBy(x => x.MediaItemId)
                .Select(x => new { x.ExternalId, Person = x.MediaItem! })
                .ToListAsync(ct);
            foreach (var r in rows) personByExtId.TryAdd(r.ExternalId, r.Person);
        }

        // Everything else goes through the name steps: lock those names first.
        var byName = list.Where(c => Id(c) is not { } x || !personByExtId.ContainsKey(x)).ToList();
        var lockKeys = byName
            .Select(c => MediaItemNormalizer.NormalizeNameLoose(c.PersonName))
            .Where(k => k.Length > 0)
            .Select(k => $"{peopleTypeId}|{k}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var held = new List<SemaphoreSlim>();
        var created = new List<MediaItem>();
        var people = new MediaItem[list.Count];
        try
        {
            foreach (var key in lockKeys)
            {
                var sem = _personNameLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
                if (await sem.WaitAsync(NameLockTimeout, ct))
                    held.Add(sem);
                else
                    logger.LogWarning(
                        "PersonResolutionService: couldn't acquire name lock {Key} within {TimeoutS}s -- " +
                        "proceeding without it (may create a duplicate stub; dedupe endpoints can clean it up later)",
                        key, NameLockTimeout.TotalSeconds);
            }

            // Steps 2/2b lookups for the whole list, read under the locks.
            var norms = byName.Select(c => MediaItemNormalizer.NormalizeName(c.PersonName)).Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal).ToList();
            var looses = byName.Select(c => MediaItemNormalizer.NormalizeNameLoose(c.PersonName)).Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal).ToList();
            // Ids by name first, then the records by primary key. Measured on the live library
            // (2026-10-02): asking SQLite for whole people records filtered by type AND name made
            // it walk the type index (300,000 people, ~800 ms a query) instead of the name index;
            // ids by name alone, with the type checked here, take a few milliseconds.
            var normHits = new List<(int Id, string Key)>();
            var looseHits = new List<(int Id, string Key)>();
            foreach (var chunk in norms.Chunk(500))
                normHits.AddRange((await db.MediaItems
                        .Where(m => chunk.Contains(m.NormalizedName!))
                        .Select(m => new { m.Id, m.NormalizedName, m.MediaTypeId }).ToListAsync(ct))
                    .Where(m => m.MediaTypeId == peopleTypeId).Select(m => (m.Id, m.NormalizedName!)));
            foreach (var chunk in looses.Chunk(500))
                looseHits.AddRange((await db.MediaItems
                        .Where(m => chunk.Contains(m.NormalizedNameLoose!))
                        .Select(m => new { m.Id, m.NormalizedNameLoose, m.MediaTypeId }).ToListAsync(ct))
                    .Where(m => m.MediaTypeId == peopleTypeId).Select(m => (m.Id, m.NormalizedNameLoose!)));

            var recordsById = new Dictionary<int, MediaItem>();
            foreach (var chunk in normHits.Concat(looseHits).Select(h => h.Id).Distinct().Chunk(500))
                foreach (var m in await db.MediaItems.Where(m => chunk.Contains(m.Id)).ToListAsync(ct))
                    recordsById[m.Id] = m;

            // Lowest id per name wins, as the original FirstOrDefault over the index did.
            var dbByNorm = normHits.OrderBy(h => h.Id).GroupBy(h => h.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => recordsById[g.First().Id], StringComparer.Ordinal);
            var dbByLoose = looseHits.OrderBy(h => h.Id).GroupBy(h => h.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => recordsById[g.First().Id], StringComparer.Ordinal);

            // Ids this source already holds for every candidate, for the same-source conflict guard.
            var candidateIds = dbByNorm.Values.Concat(dbByLoose.Values).Concat(personByExtId.Values)
                .Select(p => p.Id).Distinct().ToList();
            foreach (var chunk in candidateIds.Chunk(500))
                foreach (var x in await db.MediaExternalIds
                             .Where(x => x.Source == source && chunk.Contains(x.MediaItemId))
                             .Select(x => new { x.MediaItemId, x.ExternalId }).ToListAsync(ct))
                    (extIdsOf.TryGetValue(x.MediaItemId, out var s) ? s : extIdsOf[x.MediaItemId] = new HashSet<string>(StringComparer.Ordinal)).Add(x.ExternalId);
            foreach (var e in db.ChangeTracker.Entries<MediaExternalId>()
                         .Where(e => e.State == EntityState.Added && e.Entity.Source == source))
                (extIdsOf.TryGetValue(e.Entity.MediaItemId, out var s) ? s : extIdsOf[e.Entity.MediaItemId] = new HashSet<string>(StringComparer.Ordinal)).Add(e.Entity.ExternalId);

            // Stubs created by this batch, keyed like the database lookups (first one wins, as
            // FirstOrDefault over the ids did).
            var newByNorm = new Dictionary<string, MediaItem>(StringComparer.Ordinal);
            var newByLoose = new Dictionary<string, MediaItem>(StringComparer.Ordinal);
            var newExtIds = new Dictionary<MediaItem, HashSet<string>>(ReferenceEqualityComparer.Instance);
            bool Conflicts(MediaItem p, string? extId)
            {
                if (extId is null) return false;
                var ids = p.Id != 0 && extIdsOf.TryGetValue(p.Id, out var s) ? s
                        : newExtIds.TryGetValue(p, out var n) ? n : null;
                return ids is not null && ids.Any(x => x != extId);
            }

            for (var i = 0; i < list.Count; i++)
            {
                var c = list[i];
                var extId = Id(c);
                MediaItem? person = extId is not null && personByExtId.TryGetValue(extId, out var byId) ? byId : null;

                if (person is null)
                {
                    var norm = MediaItemNormalizer.NormalizeName(c.PersonName);
                    var loose = MediaItemNormalizer.NormalizeNameLoose(c.PersonName);
                    if (loose.Length > 0)
                    {
                        var nameMatch = dbByNorm.GetValueOrDefault(norm) ?? newByNorm.GetValueOrDefault(norm);
                        if (nameMatch is not null && !Conflicts(nameMatch, extId)) person = nameMatch;
                        if (person is null)
                        {
                            var looseMatch = dbByLoose.GetValueOrDefault(loose) ?? newByLoose.GetValueOrDefault(loose);
                            if (looseMatch is not null && !Conflicts(looseMatch, extId)) person = looseMatch;
                        }
                    }

                    if (person is null)
                    {
                        person = new MediaItem
                        {
                            MediaTypeId    = peopleTypeId,
                            Name           = c.PersonName,
                            NormalizedName = norm,
                            HierarchyLevel = 0,
                            IsStub         = true,
                            CreatedAt      = DateTime.UtcNow,
                            UpdatedAt      = DateTime.UtcNow,
                        };
                        db.MediaItems.Add(person);
                        created.Add(person);
                        newExtIds[person] = new HashSet<string>(StringComparer.Ordinal);
                        if (norm.Length > 0) newByNorm.TryAdd(norm, person);
                        if (loose.Length > 0) newByLoose.TryAdd(loose, person);
                    }
                }

                // Step 4: record the id against the person, once.
                if (extId is not null)
                {
                    var known = person.Id != 0 && extIdsOf.TryGetValue(person.Id, out var s) ? s
                              : newExtIds.TryGetValue(person, out var n) ? n : null;
                    if (known is null || !known.Contains(extId))
                    {
                        db.MediaExternalIds.Add(new MediaExternalId { MediaItem = person, Source = source, ExternalId = extId });
                        if (person.Id != 0) Attach(person, extId);
                        else { newExtIds[person].Add(extId); personByExtId.TryAdd(extId, person); }
                    }
                    else
                    {
                        personByExtId.TryAdd(extId, person);
                    }
                }
                people[i] = person;
            }

            // One save for every new stub (and the ids recorded above), still under the locks.
            if (created.Count > 0 || db.ChangeTracker.HasChanges())
                await db.SaveChangesAsync(ct);
        }
        finally
        {
            foreach (var sem in held) sem.Release();
        }

        // Step 7 for the new stubs (added, saved with the credits below).
        foreach (var person in created)
        {
            await SeedEnrichmentRowsAsync(db, person.Id, ct, save: false);
            logger.LogInformation(
                "PersonResolutionService: created new person stub {PersonId} \"{Name}\" (first credited via {Source})",
                person.Id, person.Name, source);
        }

        // Step 6: the credit rows.
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            db.MediaCredits.Add(new MediaCredit
            {
                MediaItemId       = titleMediaItemId,
                PersonName        = c.PersonName,
                Role              = c.Role,
                CharacterName     = c.CharacterName,
                BillingOrder      = c.BillingOrder,
                Source            = source,
                ExternalPersonId  = c.ExternalPersonId,
                PersonMediaItemId = people[i].Id,
            });
        }

        // Step 5: headshots, once per person (each call saves and re-resolves only if a photo is new).
        foreach (var group in list.Select((c, i) => (c.ProfileImageUrl, Person: people[i]))
                     .Where(x => !string.IsNullOrWhiteSpace(x.ProfileImageUrl))
                     .GroupBy(x => x.Person.Id))
        {
            var urls = group.Select(x => (x.ProfileImageUrl!, (string?)null)).Distinct().ToList();
            await RecordHeadshotsIfNewAsync(db, group.First().Person, urls, source, ct);
        }
    }

    /// <summary>
    /// Feed path 1 of Section 1.5 -- a person's own enrichment (Wikipedia's bio photo, TMDB's
    /// own /person/{id} profile picture plus its full alternate-photo gallery, ...) returning
    /// one or more photos for the person item directly. Previously unimplemented: only the
    /// "credited on someone else's title" feed path (above) ever wrote to person_headshots, so
    /// a person's own provider-returned photo never accumulated and PersonHeadshot stayed
    /// permanently empty for anyone who was only ever enriched directly (never yet credited on
    /// a scanned title). Confirmed live (2026-08-30) as a gap while wiring TMDB's own person
    /// endpoint. Accepts many URLs per call (not just the one primary poster) so a provider's
    /// full alternate gallery -- not just its single "current" pick -- becomes something the
    /// user can actually see and choose between.
    /// </summary>
    public async Task RecordOwnPortraitAsync(
        ChronicleDbContext db, MediaItem person, IEnumerable<(string Url, string? ThumbnailUrl)> photos,
        string source, CancellationToken ct = default)
    {
        var list = photos.Where(p => !string.IsNullOrWhiteSpace(p.Url))
            .GroupBy(p => p.Url).Select(g => g.First()).ToList();
        if (list.Count > 0)
            await RecordHeadshotsIfNewAsync(db, person, list, source, ct);
    }

    /// <summary>
    /// INSERT-only, never overwritten (see PersonHeadshot's own doc) -- a duplicate
    /// (person, url) pair is simply skipped rather than erroring, since re-discovering the same
    /// photo on re-enrichment is routine, not exceptional. Checks BOTH the database and this
    /// DbContext's own not-yet-saved Added entries -- e.g. a person credited under both Cast and
    /// Crew in the same enrichment result (an actor who's also Executive Producer) can have the
    /// identical url resolved twice within one batch, before the caller's single SaveChangesAsync
    /// at the end of its own loop -- a DB-only check can't see the first (still-pending) insert,
    /// so both get added and the unique index on (person_media_item_id, url) throws at save
    /// time. Confirmed live (2026-08-30): a real Force-refresh against TMDB hit exactly this.
    /// Batched: every genuinely-new url in <paramref name="urls"/> is inserted, then the whole
    /// batch is flushed and resolved ONCE -- calling SaveChangesAsync/ResolveAsync per url would
    /// mean one extra DB round-trip per alternate photo in a provider's full gallery (TMDB alone
    /// can return dozens).
    /// </summary>
    private async Task RecordHeadshotsIfNewAsync(
        ChronicleDbContext db, MediaItem person, IReadOnlyList<(string Url, string? ThumbnailUrl)> photos,
        string source, CancellationToken ct)
    {
        var anyNew = false;
        foreach (var (url, thumbnailUrl) in photos)
        {
            var alreadyPending = db.ChangeTracker.Entries<PersonHeadshot>().Any(e =>
                e.State == EntityState.Added &&
                e.Entity.PersonMediaItemId == person.Id && e.Entity.Url == url);
            var alreadySeen = alreadyPending || await db.PersonHeadshots.AnyAsync(
                h => h.PersonMediaItemId == person.Id && h.Url == url, ct);
            if (alreadySeen)
                continue;

            db.PersonHeadshots.Add(new PersonHeadshot
            {
                PersonMediaItemId = person.Id,
                Url               = url,
                ThumbnailUrl      = thumbnailUrl,
                Source            = source,
            });
            anyNew = true;
        }

        if (!anyNew)
            return;

        // A brand-new headshot doesn't do anything on its own -- MetadataResolutionService.
        // ResolveAsync is what actually promotes a person_headshots row onto the person's own
        // PosterUrl column (Section 1.5's special-cased poster_url resolution for "people"-type
        // items). Needs MediaType loaded -- ResolveAsync branches on item.MediaType?.Name, and
        // neither caller reliably has it populated already.
        if (person.MediaType is null)
            await db.Entry(person).Reference(p => p.MediaType).LoadAsync(ct);
        // Flush the headshot inserts first -- ResolveAsync's poster_url resolution reads
        // person_headshots back via a real query (GetLatestHeadshotUrlAsync), which (like the
        // duplicate checks above) only sees committed rows, not this same batch's still-pending
        // Add.
        await db.SaveChangesAsync(ct);
        await resolutionService.ResolveAsync(person, db, ct);
    }

    /// <summary>Steps 1-4 of the design: id lookup, then name lookup, then create-stub, then
    /// record a newly-supplied external id. Step 7 (seed enrichment rows for a brand-new stub)
    /// happens here too, right after creation -- same "new item -> pending enrichment rows for
    /// every type-compatible plugin" mechanism Add Media/type-change already use (see
    /// MediaService.MoveItemsToTypeAsync for the sibling implementation this mirrors), just
    /// invoked from this new call site.</summary>
    public async Task<MediaItem> ResolvePersonOnlyAsync(
        ChronicleDbContext db, string personName, string? externalPersonId, string source, CancellationToken ct = default)
    {
        // Step 1: external id lookup, scoped to people-type items only -- an id collision with
        // some other type's external id (astronomically unlikely given source+id are namespaced
        // per plugin, but cheap to guard explicitly) must never resolve onto a non-person item.
        MediaItem? person = null;
        if (!string.IsNullOrWhiteSpace(externalPersonId))
        {
            person = await db.MediaExternalIds
                .Include(x => x.MediaItem)
                .Where(x => x.Source == source && x.ExternalId == externalPersonId)
                .Select(x => x.MediaItem)
                .FirstOrDefaultAsync(m => m != null && m.MediaType!.Name == "people", ct);
        }

        var peopleTypeId = await GetPeopleMediaTypeIdAsync(db, ct);
        var normalized = MediaItemNormalizer.NormalizeName(personName);
        var looseNormalized = MediaItemNormalizer.NormalizeNameLoose(personName);

        // Steps 2/2b/3 (name lookup, loose-name lookup, create-stub) run under a per-name lock.
        // Confirmed live (2026-09-03): TMDB's and Wikipedia's own enrichment passes for the same
        // title ("Connect") each resolved "Jung Hae-in" independently, 82ms apart, on separate
        // DbContexts -- two DIFFERENT plugins enriching the SAME title concurrently, neither
        // able to see the other's not-yet-committed insert, so Step 2's exact-match SELECT found
        // nothing on both sides and both created a stub. MetadataEnrichmentService's own
        // per-(item,plugin) and per-plugin locks don't cover this: they only stop the SAME
        // plugin racing itself, not two different plugins touching the same item at once. This
        // is a genuinely different failure mode from the same-name-different-real-person case
        // Step 2's own conflict guard protects against just below (that's about two DIFFERENT
        // real people who happen to share a name; this is one real person resolved twice at
        // once) -- so it needs a different fix: serialize the check-then-insert instead of
        // trying to detect the collision after the fact. Keyed on the loose-normalized name (not
        // the exact one) so a same-instant race between differently-spaced spellings of the same
        // name -- the exact scenario NormalizedNameLoose/Step 2b exists for -- also serializes
        // against itself, not just the identical-spelling case.
        if (person is null && looseNormalized.Length > 0)
        {
            var lockKey = $"{peopleTypeId}|{looseNormalized}";
            var nameSem = _personNameLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
            var acquired = await nameSem.WaitAsync(NameLockTimeout, ct);
            if (!acquired)
            {
                logger.LogWarning(
                    "PersonResolutionService: couldn't acquire name lock for \"{Name}\" within {TimeoutS}s -- " +
                    "proceeding without it (may create a duplicate stub; dedupe endpoints can clean it up later)",
                    personName, NameLockTimeout.TotalSeconds);
            }
            try
            {
                // Step 2: name lookup, scoped to people-type items.
                var nameMatch = await db.MediaItems.FirstOrDefaultAsync(
                    m => m.MediaTypeId == peopleTypeId && m.NormalizedName == normalized, ct);

                // Before trusting a name-only match, check whether this credit's own SOURCE
                // already has a DIFFERENT external id recorded against the name-matched person --
                // e.g. it already carries tmdb:9402 and this credit brings tmdb:84008. Two
                // different ids from the same source for what's supposedly one real person is the
                // exact signature of a same-name collision, not routine multi-source enrichment:
                // confirmed live, "Brian Johnson" merged 4 unrelated real people this way (a VFX
                // artist, the AC/DC singer, and two more) purely because none of their early
                // credits carried an id yet to catch it on -- and once the first stray id got
                // welded on via Step 4 below, every later credit for THAT id kept reinforcing the
                // same wrong person. When a same-source conflict is found, don't attach -- fall
                // through to Step 3 and create a new person instead, even though this means two
                // credits with no id at all for what might genuinely be the same real person can
                // now land on separate stubs. That's the safer failure direction: a wrongly-split
                // person is visible and fixable (a thin duplicate stub); a wrongly-merged person
                // silently corrupts another real person's page and is easy to never notice.
                var hasConflictingSourceId = nameMatch is not null && !string.IsNullOrWhiteSpace(externalPersonId) &&
                    await HasConflictingSourceIdAsync(db, nameMatch.Id, source, externalPersonId, ct);

                if (!hasConflictingSourceId)
                    person = nameMatch;

                // Step 2b: loose name lookup (whitespace-insensitive), scoped to people-type
                // items. Confirmed live (2026-09-03): "Cee Lo Green" (from one plugin) and
                // "CeeLo Green" (from another) are the same real person, but Step 2's exact
                // NormalizedName match treats "cee lo green" and "ceelo green" as different
                // strings -- NormalizeName collapses whitespace runs to a single space, it never
                // removes it, so a plugin that spaces a name differently than an earlier one
                // always creates a fresh duplicate stub instead of matching the existing person.
                // Queries the persisted NormalizedNameLoose column directly (kept in sync by
                // ChronicleDbContext.SyncNormalizedNames on every write path) rather than
                // stripping spaces from NormalizedName at query time, so this stays a plain
                // indexed-equality lookup with one source of truth for what "loose" means. Same
                // same-source-conflict guard as Step 2 -- this must never become a second way to
                // blindly merge two different real people.
                if (person is null)
                {
                    var looseMatch = await db.MediaItems.FirstOrDefaultAsync(
                        m => m.MediaTypeId == peopleTypeId && m.NormalizedNameLoose == looseNormalized, ct);

                    var hasConflictingSourceIdLoose = looseMatch is not null && !string.IsNullOrWhiteSpace(externalPersonId) &&
                        await HasConflictingSourceIdAsync(db, looseMatch.Id, source, externalPersonId, ct);

                    if (!hasConflictingSourceIdLoose)
                        person = looseMatch;
                }

                // Step 3: create a new stub. Still inside the lock -- the whole point is that
                // no other caller's Step 2/2b can run (and miss this insert) between the lookup
                // above and this create-and-commit.
                if (person is null)
                {
                    person = new MediaItem
                    {
                        MediaTypeId    = peopleTypeId,
                        Name           = personName,
                        NormalizedName = normalized,
                        HierarchyLevel = 0,
                        IsStub         = true,
                        CreatedAt      = DateTime.UtcNow,
                        UpdatedAt      = DateTime.UtcNow,
                    };
                    db.MediaItems.Add(person);
                    await db.SaveChangesAsync(ct); // need the id before seeding enrichment rows / recording the external id

                    await SeedEnrichmentRowsAsync(db, person.Id, ct);

                    logger.LogInformation(
                        "PersonResolutionService: created new person stub {PersonId} \"{Name}\" (first credited via {Source})",
                        person.Id, personName, source);
                }
            }
            finally
            {
                if (acquired) nameSem.Release();
            }
        }

        // Fallback create, unlocked: only reachable when personName normalizes down to an empty
        // loose form (pure punctuation/whitespace, e.g. "..."), so the locked Steps 2/2b/3 block
        // above never ran at all -- there's no meaningful name to look up or race on in that
        // case, just create the stub directly. ResolveAndRecordCreditAsync already rejects a
        // blank personName before calling this method, so this is a rare edge case, not the
        // normal path.
        if (person is null)
        {
            person = new MediaItem
            {
                MediaTypeId    = peopleTypeId,
                Name           = personName,
                NormalizedName = normalized,
                HierarchyLevel = 0,
                IsStub         = true,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };
            db.MediaItems.Add(person);
            await db.SaveChangesAsync(ct); // need the id before seeding enrichment rows / recording the external id

            await SeedEnrichmentRowsAsync(db, person.Id, ct);

            logger.LogInformation(
                "PersonResolutionService: created new person stub {PersonId} \"{Name}\" (first credited via {Source})",
                person.Id, personName, source);
        }

        // Step 4: record the external id, if new. Same not-yet-saved-entries check as the
        // headshot one above, for the same reason (a person resolved twice in one batch,
        // before the caller's single end-of-loop SaveChangesAsync).
        if (!string.IsNullOrWhiteSpace(externalPersonId))
        {
            var alreadyPending = db.ChangeTracker.Entries<MediaExternalId>().Any(e =>
                e.State == EntityState.Added &&
                e.Entity.MediaItemId == person.Id && e.Entity.Source == source && e.Entity.ExternalId == externalPersonId);
            var alreadyRecorded = alreadyPending || await db.MediaExternalIds.AnyAsync(
                x => x.MediaItemId == person.Id && x.Source == source && x.ExternalId == externalPersonId, ct);
            if (!alreadyRecorded)
            {
                db.MediaExternalIds.Add(new MediaExternalId
                {
                    MediaItemId = person.Id,
                    Source      = source,
                    ExternalId  = externalPersonId,
                });
            }
        }

        return person;
    }

    /// <summary>
    /// Root-caused live (2026-09-18): the same-source-conflict guard in Steps 2/2b (see their
    /// own doc) only ever queried the database -- but <see cref="ResolveCreditsAsync"/> (the
    /// only real caller, in MetadataEnrichmentService) resolves an ENTIRE title's cast+crew list
    /// in one loop against one DbContext, calling SaveChangesAsync exactly once at the end. So
    /// when a title's own credit list happened to include two DIFFERENT real people who share an
    /// exact name (extremely common for names like "Chris Clark" or "Adam Jones" once you're
    /// crediting dozens of crew per title across a whole library) -- the FIRST one's external id
    /// (added via Step 4 below) sat as a pending, unsaved <see cref="MediaExternalId"/> in this
    /// DbContext's change tracker when the SECOND one's guard query ran, so the guard's
    /// database-only query saw no conflict and silently merged them anyway. Confirmed live: 2,519
    /// "people" items ended up carrying multiple different same-source external ids this exact
    /// way (e.g. the actor Christopher Lee's item also carrying an unrelated TMDB production
    /// assistant's id, whose photo then became the item's displayed poster). Same
    /// not-yet-saved-entries pattern already used by Step 4's own duplicate check and
    /// RecordHeadshotsIfNewAsync just above -- checking pending Added entries first, database
    /// second, so this can never miss a conflict from earlier in the very same unsaved batch.
    /// </summary>
    private static async Task<bool> HasConflictingSourceIdAsync(
        ChronicleDbContext db, int personId, string source, string externalPersonId, CancellationToken ct)
    {
        var pendingConflict = db.ChangeTracker.Entries<MediaExternalId>().Any(e =>
            e.State == EntityState.Added &&
            e.Entity.MediaItemId == personId && e.Entity.Source == source && e.Entity.ExternalId != externalPersonId);
        if (pendingConflict)
            return true;

        return await db.MediaExternalIds.AnyAsync(x =>
            x.MediaItemId == personId && x.Source == source && x.ExternalId != externalPersonId, ct);
    }

    /// <summary>
    /// Unlike every other media type, "people" has no single plugin that owns registering it --
    /// cast/crew credits come from whichever provider (TMDB, MusicBrainz, Hardcover, ...) is
    /// enriching the title being credited, so nothing in the normal plugin-media-type-sync path
    /// (PluginHostService.SyncMediaTypesFromPluginsAsync) ever creates this row. Self-heals by
    /// creating it here, the first time anyone is ever credited, instead of requiring an install
    /// step that doesn't otherwise exist. IsTrackable = false: a person is a reference/catalog
    /// entry credited on other media, never something tracked/watched on its own -- see
    /// MediaType.IsTrackable.
    /// </summary>
    private static async Task<int> GetPeopleMediaTypeIdAsync(ChronicleDbContext db, CancellationToken ct)
    {
        var id = await db.MediaTypes.Where(t => t.Name == "people").Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
        if (id is not null)
            return id.Value;

        var mediaType = new MediaType
        {
            Name            = "people",
            DisplayName     = "People",
            HierarchyLevels = 1,
            InteractionVerb = "viewed",
            ProgressUnit    = "percent",
            IsBuiltIn       = true,
            IsActive        = true,
            IsTrackable     = false,
            CreatedAt       = DateTime.UtcNow,
        };
        db.MediaTypes.Add(mediaType);
        try
        {
            await db.SaveChangesAsync(ct);
            return mediaType.Id;
        }
        catch (DbUpdateException)
        {
            // Lost a race with another concurrent credit resolution also creating this row --
            // the unique index on Name rejected ours; fall back to whichever one won.
            db.ChangeTracker.Clear();
            return await db.MediaTypes.Where(t => t.Name == "people").Select(t => t.Id).FirstAsync(ct);
        }
    }

    private async Task SeedEnrichmentRowsAsync(ChronicleDbContext db, int personMediaItemId, CancellationToken ct, bool save = true)
    {
        var toAdd = new List<MediaItemEnrichment>();
        foreach (var (pluginId, provider, _) in pluginRegistry.GetMetadataProviderEntries())
        {
            var supported = provider.GetSupportedMediaTypes()
                .Any(t => string.Equals(t.MediaTypeName, "people", StringComparison.OrdinalIgnoreCase));
            if (!supported) continue;

            toAdd.Add(new MediaItemEnrichment
            {
                MediaItemId = personMediaItemId,
                PluginId    = pluginId,
                Status      = EnrichmentStatus.Pending,
                MaxRetries  = 3,
            });
        }

        if (toAdd.Count > 0)
        {
            db.MediaEnrichments.AddRange(toAdd);
            if (save) await db.SaveChangesAsync(ct);
        }
    }
}
