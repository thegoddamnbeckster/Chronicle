using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.API.DTOs;
using Chronicle.Core.Helpers;
using Chronicle.Data;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers
{
    /// <summary>
    /// Catalog-wide People endpoints -- see docs/plans/2026-08-28-people-section-design.md.
    /// People are MediaTypeName == "people" MediaItems (Section 1.1), listed unfiltered by any
    /// single user's library (Section 1.4 -- no watch-status concept applies to a person). A
    /// person's own detail reuses the existing generic GET /api/v1/media/{id}; this controller
    /// only covers what that endpoint doesn't: the catalog-wide list and role-grouped credits.
    /// </summary>
    [ApiController]
    [Route("api/v1/people")]
    [Authorize]
    public class PeopleController : ControllerBase
    {
        private readonly ChronicleDbContext _context;
        private readonly IPersonResolutionService _personResolutionService;

        public PeopleController(ChronicleDbContext context, IPersonResolutionService personResolutionService)
        {
            _context = context;
            _personResolutionService = personResolutionService;
        }

        /// <summary>Shared ordering/filtering pass behind both GetPeople and GetJumpPosition --
        /// the full people-type catalog (after role/deceased filters), sorted by last name.
        /// Sorted by last name -- per-user request (2026-08-31): "delete the sorting and keep
        /// it alphabetical by last name." There's no separate first/last-name column (a person
        /// is just a MediaItem with one Name field), and deriving a "last name" from free text
        /// (particles like "del"/"van", generational suffixes) isn't reliably expressible as
        /// translatable SQL across both supported providers (SQLite/PostgreSQL) -- so the
        /// ordering key is computed in memory via PersonNameHelper, the same helper applied to
        /// a jump target so the two compare on the same key space. Only Id+Name is projected
        /// for this pass (cheap even for a large catalog); callers fetch full rows for just
        /// their page separately.</summary>
        private async Task<List<(int Id, string Key)>> GetOrderedPeopleAsync(
            int peopleTypeId, string? role, bool? deceased, CancellationToken ct)
        {
            var query = _context.MediaItems.Where(m => m.MediaTypeId == peopleTypeId);

            if (deceased == true) query = query.Where(m => m.DeathDate != null);
            else if (deceased == false) query = query.Where(m => m.DeathDate == null);

            if (!string.IsNullOrWhiteSpace(role))
                query = query.Where(m => _context.MediaCredits.Any(c => c.PersonMediaItemId == m.Id && c.Role == role));

            var idsAndNames = await query
                .Select(m => new { m.Id, m.Name })
                .ToListAsync(ct);

            return idsAndNames
                .Select(m => (m.Id, Key: PersonNameHelper.ToLastNameFirstSortKey(m.Name)))
                .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Id)
                .ToList();
        }

        [HttpGet]
        public async Task<IActionResult> GetPeople(
            [FromQuery] string? role = null,
            [FromQuery] bool? deceased = null,
            [FromQuery] int page = 1,
            [FromQuery] int perPage = 60,
            CancellationToken ct = default)
        {
            var peopleTypeId = await GetPeopleMediaTypeIdAsync(ct);
            if (peopleTypeId is null)
                return Ok(ApiResponse<List<PersonListItemDto>>.Ok([], new PaginationInfo(page, perPage, 0)));

            var ordered = await GetOrderedPeopleAsync(peopleTypeId.Value, role, deceased, ct);
            var total = ordered.Count;
            var pageIds = ordered.Skip((page - 1) * perPage).Take(perPage).Select(m => m.Id).ToList();

            var itemsById = await _context.MediaItems
                .Where(m => pageIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, ct);
            var items = pageIds.Where(itemsById.ContainsKey).Select(id => itemsById[id]).ToList();

            var itemIds = items.Select(i => i.Id).ToList();
            var rolesByPerson = await _context.MediaCredits
                .Where(c => c.PersonMediaItemId != null && itemIds.Contains(c.PersonMediaItemId.Value))
                .Select(c => new { c.PersonMediaItemId, c.Role })
                .Distinct()
                .ToListAsync(ct);
            var rolesLookup = rolesByPerson
                .GroupBy(x => x.PersonMediaItemId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Role).OrderBy(r => r).ToList());

            var dtos = items.Select(m => new PersonListItemDto(
                m.Id, m.Name, m.PosterUrl, m.BirthDate, m.DeathDate,
                rolesLookup.GetValueOrDefault(m.Id, [])
            )).ToList();

            return Ok(ApiResponse<List<PersonListItemDto>>.Ok(dtos, new PaginationInfo(page, perPage, total)));
        }

        /// <summary>
        /// Resolves a jump-to-name target (the A-Z rail or the jump-search box) to its 0-based
        /// position within the FULL ordered list (plus that list's total count), instead of
        /// GetPeople itself truncating the list down to "target and everything after" the way
        /// it originally did. That truncation made `total` only ever cover the remainder, so the
        /// People page's infinite-scroll list had nothing loaded before the jumped-to person and
        /// could never scroll back up past them -- per-user request (2026-09-03): "what is the
        /// possibility of having the ability to scroll up when you click up people from a
        /// person's detail page... it just sticks that person at the top." The frontend uses the
        /// returned index to compute which GetPeople page to open on (index / perPage + 1) and
        /// where within that page the target sits, then pages both forward and backward from
        /// there via GetPeople's own ordinary page/perPage -- GetPeople no longer needs to know
        /// about jumping at all.
        /// </summary>
        [HttpGet("jump-position")]
        public async Task<IActionResult> GetJumpPosition(
            [FromQuery] string jumpTo,
            [FromQuery] string? role = null,
            [FromQuery] bool? deceased = null,
            CancellationToken ct = default)
        {
            var peopleTypeId = await GetPeopleMediaTypeIdAsync(ct);
            if (peopleTypeId is null)
                return Ok(ApiResponse<object>.Ok(new { index = 0, total = 0 }));

            var ordered = await GetOrderedPeopleAsync(peopleTypeId.Value, role, deceased, ct);
            var target = PersonNameHelper.ToLastNameFirstSortKey(jumpTo);
            var index = ordered.FindIndex(m => string.Compare(m.Key, target, StringComparison.OrdinalIgnoreCase) >= 0);
            if (index < 0) index = ordered.Count; // past the end of the list -- lands at the bottom

            return Ok(ApiResponse<object>.Ok(new { index, total = ordered.Count }));
        }

        /// <summary>Distinct role values across every resolved credit -- feeds the People page's
        /// role filter multi-select (Section 5). A dedicated small endpoint rather than bundling
        /// this into the list response, per the design's own "or a dedicated small endpoint"
        /// note -- the role set changes far less often than the page itself, and every page/
        /// filter combination would otherwise recompute it identically.</summary>
        [HttpGet("roles")]
        public async Task<IActionResult> GetRoles(CancellationToken ct)
        {
            var roles = await _context.MediaCredits
                .Where(c => c.PersonMediaItemId != null)
                .Select(c => c.Role)
                .Distinct()
                .OrderBy(r => r)
                .ToListAsync(ct);
            return Ok(ApiResponse<List<string>>.Ok(roles));
        }

        [HttpGet("{id:int}/credits")]
        public async Task<IActionResult> GetCredits(int id, CancellationToken ct)
        {
            var credits = await _context.MediaCredits
                .Where(c => c.PersonMediaItemId == id)
                .Include(c => c.MediaItem).ThenInclude(m => m!.MediaType)
                .ToListAsync(ct);

            var groups = credits
                .GroupBy(c => c.Role)
                .OrderBy(g => g.Key)
                .Select(g => new PersonCreditGroupDto(
                    g.Key,
                    // Same title can be resolved onto this person more than once across
                    // sources (e.g. TMDB and a legacy Trakt import both credited "Actor" on
                    // the same movie) -- DistinctBy the title, not the (title, source) pair,
                    // since the detail page shows one card per title, not one per source.
                    g.Select(c => new PersonCreditDto(
                        c.MediaItemId, c.MediaItem.Name, c.MediaItem.PosterUrl, c.MediaItem.Year,
                        c.MediaItem.MediaType?.Name ?? string.Empty, c.CharacterName))
                     .DistinctBy(c => c.MediaItemId)
                     // Most recent first, per-user request (2026-08-30) -- a credit with no
                     // known year sorts last (Year ?? int.MinValue) rather than as "earliest".
                     .OrderByDescending(c => c.Year ?? int.MinValue)
                     .ToList()
                )).ToList();

            return Ok(ApiResponse<List<PersonCreditGroupDto>>.Ok(groups));
        }

        /// <summary>Every accumulated photo for this person (person_headshots -- Section 1.5),
        /// newest-discovered first, for the detail page's photo picker. Answers "surely there's
        /// more than one" by actually surfacing every headshot Chronicle has ever recorded for
        /// them, not just whichever one happens to be currently resolved onto PosterUrl.</summary>
        [HttpGet("{id:int}/headshots")]
        public async Task<IActionResult> GetHeadshots(int id, CancellationToken ct)
        {
            var posterUrl = await _context.MediaItems
                .Where(m => m.Id == id)
                .Select(m => m.PosterUrl)
                .FirstOrDefaultAsync(ct);

            var headshots = await _context.PersonHeadshots
                .Where(h => h.PersonMediaItemId == id)
                .OrderByDescending(h => h.FirstSeenAt)
                .Select(h => new PersonHeadshotDto(
                    h.Id, h.Url, h.ThumbnailUrl, h.Source, h.FirstSeenAt, h.Url == posterUrl))
                .ToListAsync(ct);

            return Ok(ApiResponse<List<PersonHeadshotDto>>.Ok(headshots));
        }

        private async Task<int?> GetPeopleMediaTypeIdAsync(CancellationToken ct) =>
            await _context.MediaTypes.Where(t => t.Name == "people").Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);

        /// <summary>
        /// Re-resolves any credit whose person link was cleared (PersonMediaItemId == null) --
        /// e.g. after deleting a Person record that had wrongly conflated multiple real people
        /// under one shared name (see PersonResolutionService.ResolvePersonOnlyAsync's
        /// same-source-conflict guard, added to stop this happening for NEW credits; this
        /// endpoint is what re-derives the ones already sitting orphaned from before the guard
        /// existed). Re-derives fresh from each affected item's already-cached per-plugin
        /// metadata_json cast/crew array -- pure local reprocessing, no live provider re-fetch,
        /// same technique PluginHostService.BackfillCreditsFromCachedMetadataAsync uses for its
        /// own one-time historical backfill. Clears and re-inserts the FULL credit set for each
        /// affected (item, source) pair (not just the orphaned rows) so a partially-resolved
        /// pair can't end up with duplicate rows once re-derived. Admin only: maintenance
        /// operation, not something a regular user triggers.
        /// </summary>
        [HttpPost("reprocess-orphaned-credits")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ReprocessOrphanedCredits(CancellationToken ct)
        {
            var affectedItemIds = await _context.MediaCredits
                .Where(c => c.PersonMediaItemId == null)
                .Select(c => c.MediaItemId)
                .Distinct()
                .ToListAsync(ct);

            int itemsProcessed = 0, pairsReprocessed = 0, creditsResolved = 0;

            foreach (var itemId in affectedItemIds)
            {
                ct.ThrowIfCancellationRequested();

                var item = await _context.MediaItems
                    .Where(m => m.Id == itemId)
                    .Select(m => new { m.Id, m.MetadataJson })
                    .FirstOrDefaultAsync(ct);
                if (item?.MetadataJson is null) continue;

                // Only (item, source) pairs that currently have an orphaned credit get cleared
                // and re-derived -- a pair with no orphaned credit is left untouched.
                var orphanedSources = await _context.MediaCredits
                    .Where(c => c.MediaItemId == itemId && c.PersonMediaItemId == null)
                    .Select(c => c.Source)
                    .Distinct()
                    .ToListAsync(ct);
                if (orphanedSources.Count == 0) continue;

                Dictionary<string, JsonElement>? blobs;
                try { blobs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.MetadataJson); }
                catch (JsonException) { continue; }
                if (blobs is null) continue;

                itemsProcessed++;

                foreach (var (pluginId, blob) in blobs)
                {
                    if (pluginId is "_resolved" or "_overrides") continue;
                    if (blob.ValueKind != JsonValueKind.Object) continue;

                    var source = PluginIdHelper.ToSource(pluginId);
                    if (!orphanedSources.Contains(source)) continue;

                    // Clear the FULL existing set for this pair first -- re-deriving from the
                    // cached blob recreates it completely, so leaving old rows in place would
                    // duplicate whichever credits were already correctly resolved.
                    var existing = await _context.MediaCredits
                        .Where(c => c.MediaItemId == itemId && c.Source == source)
                        .ToListAsync(ct);
                    _context.MediaCredits.RemoveRange(existing);
                    pairsReprocessed++;

                    var hasCast = blob.TryGetProperty("cast", out var castEl) && castEl.ValueKind == JsonValueKind.Array && castEl.GetArrayLength() > 0;
                    var hasCrew = blob.TryGetProperty("crew", out var crewEl) && crewEl.ValueKind == JsonValueKind.Array && crewEl.GetArrayLength() > 0;

                    if (hasCast)
                    {
                        List<CastMember>? cast = null;
                        try { cast = JsonSerializer.Deserialize<List<CastMember>>(castEl.GetRawText()); }
                        catch (JsonException) { }

                        var billingOrder = 0;
                        foreach (var c in cast ?? [])
                        {
                            if (string.IsNullOrWhiteSpace(c.Name)) continue;
                            await _personResolutionService.ResolveAndRecordCreditAsync(
                                _context, itemId, c.Name, c.ExternalPersonId, source, c.ProfileImageUrl,
                                role: "Actor", characterName: c.Role, billingOrder: billingOrder++, ct);
                            creditsResolved++;
                        }
                    }

                    if (hasCrew)
                    {
                        List<CrewMember>? crew = null;
                        try { crew = JsonSerializer.Deserialize<List<CrewMember>>(crewEl.GetRawText()); }
                        catch (JsonException) { }

                        foreach (var c in crew ?? [])
                        {
                            if (string.IsNullOrWhiteSpace(c.Name)) continue;
                            await _personResolutionService.ResolveAndRecordCreditAsync(
                                _context, itemId, c.Name, c.ExternalPersonId, source, c.ProfileImageUrl,
                                role: c.Job ?? "Crew", characterName: null, billingOrder: null, ct);
                            creditsResolved++;
                        }
                    }
                }

                await _context.SaveChangesAsync(ct);
                // Same reason PluginHostService.BackfillCreditsFromCachedMetadataAsync clears
                // after every page: this request can touch thousands of items in one call, and
                // an EF change tracker that never gets reset keeps accumulating every entity
                // it's ever seen, making each subsequent SaveChangesAsync's diff progressively
                // slower -- confirmed live, this endpoint dropped from ~220 credits/min to
                // ~17/min over its first ~8 minutes before this fix.
                _context.ChangeTracker.Clear();
            }

            return Ok(ApiResponse<object>.Ok(new { itemsProcessed, pairsReprocessed, creditsResolved }));
        }

        private static readonly Regex TmdbPersonExternalIdRe = new(@"^(?:person|tmdb):(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// One-time historical backfill for a real production bug (fixed in
        /// MediaItemNormalizer.NormalizeName, 2026-09-03): the stored NormalizedName column
        /// was computed without Unicode-normalizing the input first, so the same visible name
        /// arriving in two different Unicode composition forms (a precomposed accented letter
        /// vs. a base letter plus a combining mark) produced two different NormalizedName
        /// values and could never be recognized as the same person by
        /// PersonResolutionService's own name-lookup. This recomputes NormalizedName for every
        /// "people" item using the now-fixed logic; only rows whose value actually changes are
        /// written. Doesn't touch anything else -- run dedupe-by-name cleanup separately (and
        /// carefully: unlike a shared external id, a shared normalized name alone is NOT proof
        /// of the same real person, see the Brian Johnson/Jesse James/Jonathan Lee cases this
        /// session -- corroborate with matching credits or another external id before deleting
        /// anything found this way). Admin only: maintenance operation, not something a
        /// regular user triggers.
        /// </summary>
        [HttpPost("backfill-normalized-names")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> BackfillNormalizedNames(CancellationToken ct)
        {
            var peopleTypeId = await GetPeopleMediaTypeIdAsync(ct);
            if (peopleTypeId is null)
                return Ok(ApiResponse<object>.Ok(new { updated = 0 }));

            var people = await _context.MediaItems
                .Where(m => m.MediaTypeId == peopleTypeId.Value)
                .Select(m => new { m.Id, m.Name, m.NormalizedName })
                .ToListAsync(ct);

            int updated = 0;
            foreach (var p in people)
            {
                ct.ThrowIfCancellationRequested();
                var correct = MediaItemNormalizer.NormalizeName(p.Name);
                if (correct == p.NormalizedName) continue;

                await _context.MediaItems
                    .Where(m => m.Id == p.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.NormalizedName, correct), ct);
                updated++;
            }

            return Ok(ApiResponse<object>.Ok(new { updated }));
        }

        // Mirrors WikipediaScoring.StripDisambiguationSuffix's own regex exactly (that plugin
        // project isn't referenced here) -- see the backfill endpoint just below for why this
        // needs a one-time pass over already-written rows, not just the plugin-side fix.
        private static readonly System.Text.RegularExpressions.Regex DisambiguationSuffixRe =
            new(@"\s*\([^)]*\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// One-time cleanup for person names still carrying a Wikipedia disambiguation
        /// parenthetical -- "Aaron Douglas (actor)" instead of "Aaron Douglas". The plugin-side
        /// fix (chronicle.plugin.wikipedia v1.0.4, 2026-09-02: WikipediaScoring.
        /// StripDisambiguationSuffix) already stops this for every enrichment from here on, but
        /// it doesn't retroactively fix a person whose Name was already overwritten by an
        /// enrichment pass that ran BEFORE that fix landed -- confirmed live (2026-09-03):
        /// "Aaron Douglas (actor)"'s own UpdatedAt (2026-08-31) predates the fix (2026-09-02).
        /// Scoped to people with a recorded wikipedia external id, since that parenthetical is a
        /// Wikipedia-specific convention -- a non-Wikipedia-sourced name that happens to contain
        /// parentheses for some other legitimate reason must never be touched.
        /// </summary>
        [HttpPost("strip-wikipedia-disambiguation-suffix")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> StripWikipediaDisambiguationSuffix(CancellationToken ct)
        {
            var peopleTypeId = await GetPeopleMediaTypeIdAsync(ct);
            if (peopleTypeId is null)
                return Ok(ApiResponse<object>.Ok(new { updated = 0 }));

            var wikipediaPersonIds = await _context.MediaExternalIds
                .Where(x => x.Source == "wikipedia")
                .Select(x => x.MediaItemId)
                .Distinct()
                .ToListAsync(ct);

            var people = await _context.MediaItems
                .Where(m => m.MediaTypeId == peopleTypeId.Value && wikipediaPersonIds.Contains(m.Id))
                .Select(m => new { m.Id, m.Name })
                .ToListAsync(ct);

            int updated = 0;
            foreach (var p in people)
            {
                ct.ThrowIfCancellationRequested();
                var stripped = DisambiguationSuffixRe.Replace(p.Name, string.Empty);
                if (stripped == p.Name || stripped.Length == 0) continue;

                await _context.MediaItems
                    .Where(m => m.Id == p.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.Name, stripped)
                        .SetProperty(m => m.NormalizedName, MediaItemNormalizer.NormalizeName(stripped))
                        .SetProperty(m => m.NormalizedNameLoose, MediaItemNormalizer.NormalizeNameLoose(stripped)), ct);
                updated++;
            }

            return Ok(ApiResponse<object>.Ok(new { updated }));
        }

        /// <summary>
        /// One-time backfill for MediaItem.NormalizedNameLoose (added 2026-09-03 alongside
        /// PersonResolutionService's Step 2b loose-name fallback -- see the doc comment there).
        /// ChronicleDbContext.SyncNormalizedNames keeps this column current for every write from
        /// here on, but a migration only adds the column -- it doesn't compute values for the
        /// existing rows, and Step 2b's query would silently match nothing against pre-existing
        /// people until each one happens to be re-saved for an unrelated reason. Scoped to all
        /// MediaItems, not just people, since the column lives on MediaItem generally and the
        /// same central hook maintains it for every media type.
        /// </summary>
        [HttpPost("backfill-normalized-name-loose")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> BackfillNormalizedNameLoose(CancellationToken ct)
        {
            var items = await _context.MediaItems
                .Select(m => new { m.Id, m.Name, m.NormalizedNameLoose })
                .ToListAsync(ct);

            int updated = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var correct = MediaItemNormalizer.NormalizeNameLoose(item.Name);
                if (correct == item.NormalizedNameLoose) continue;

                await _context.MediaItems
                    .Where(m => m.Id == item.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.NormalizedNameLoose, correct), ct);
                updated++;
            }

            return Ok(ApiResponse<object>.Ok(new { updated }));
        }

        /// <summary>
        /// Companion to dedupe-tmdb-duplicates below, and must run BEFORE it (or before any
        /// fresh credit reprocessing): rewrites every remaining "person:N"-format MediaExternalId
        /// (Source="tmdb") on a "people" item to the "tmdb:N" format every other code path
        /// already expects for that column. Discovered live (2026-09-03) running
        /// reprocess-orphaned-credits AFTER dedupe-tmdb-duplicates: PersonResolutionService's own
        /// same-name-match safety check (added for the Brian Johnson conflation fix) sees
        /// "person:1851642" vs a fresh credit's "tmdb:1851642" as a same-source CONFLICT --
        /// exactly the signature it's designed to catch for two different real people sharing a
        /// name -- and creates a brand-new duplicate stub rather than reusing the existing
        /// record. Every surviving "person:N" row is a live landmine for this: the very fix that
        /// stops blind merges was, via this formatting mismatch, actively manufacturing fresh
        /// duplicates (confirmed: Sheila Atim and others) every time credits got reprocessed.
        /// When an item already carries both formats (harmless leftover from before either fix),
        /// the redundant "person:N" row is deleted rather than renamed onto a collision.
        /// </summary>
        [HttpPost("normalize-tmdb-person-ids")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> NormalizeTmdbPersonIds(CancellationToken ct)
        {
            var peopleTypeId = await GetPeopleMediaTypeIdAsync(ct);
            if (peopleTypeId is null)
                return Ok(ApiResponse<object>.Ok(new { renamed = 0, deletedRedundant = 0 }));

            // Pure SQL-side bulk operations (ExecuteDelete/ExecuteUpdate), not tracked-entity
            // mutation -- a first version of this endpoint loaded every "person:"-format row
            // into the change tracker up front, then mutated each one in a loop that called
            // ChangeTracker.Clear() every 500 rows for throughput. That's exactly wrong for
            // direct property mutation (unlike Remove(), which re-attaches on demand): once an
            // entity is detached by Clear(), setting a property on it does nothing EF will ever
            // persist. Confirmed live (2026-09-03): the endpoint reported 75,145 "renamed" but
            // only the first batch actually reached the database -- Sheila Atim's own record
            // was silently left as "person:1851642", which is exactly what let a fresh
            // duplicate get created for her moments later. ExecuteUpdate/ExecuteDelete compile
            // to plain SQL statements and have no such tracking-lifetime pitfall.
            var deletedRedundant = await _context.MediaExternalIds
                .Where(e => e.Source == "tmdb"
                         && e.ExternalId.StartsWith("person:")
                         && _context.MediaItems.Any(m => m.Id == e.MediaItemId && m.MediaTypeId == peopleTypeId.Value)
                         && _context.MediaExternalIds.Any(o => o.MediaItemId == e.MediaItemId
                                                             && o.Source == "tmdb"
                                                             && o.ExternalId.StartsWith("tmdb:")))
                .ExecuteDeleteAsync(ct);

            var renamed = await _context.MediaExternalIds
                .Where(e => e.Source == "tmdb"
                         && e.ExternalId.StartsWith("person:")
                         && _context.MediaItems.Any(m => m.Id == e.MediaItemId && m.MediaTypeId == peopleTypeId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ExternalId, e => "tmdb:" + e.ExternalId.Substring(7)), ct);

            return Ok(ApiResponse<object>.Ok(new { renamed, deletedRedundant }));
        }

        /// <summary>
        /// One-time historical cleanup for a real production bug (fixed in
        /// MetadataEnrichmentService.UpsertExternalIdForEnrichmentAsync, 2026-09-03): TMDB
        /// person ids were written under two different (Source="tmdb", ExternalId=...) shapes
        /// -- "person:N" from direct TMDB enrichment vs "tmdb:N" from credit-derived stub
        /// creation -- which defeated the "is this id already owned by another item" duplicate
        /// check and let a second Person stub get created for essentially every person touched
        /// by both paths. Confirmed live: 39,000+ duplicate groups across the catalog.
        ///
        /// Groups every "people" item by its underlying TMDB numeric person id (regardless of
        /// which prefix it's stored under) and, for any group spanning more than one MediaItem,
        /// keeps exactly one and deletes the rest -- per [[feedback_chronicle_dedup_delete_not_merge]],
        /// delete rather than merge. Winner priority: a pinned artwork override beats
        /// everything (never discard a user's own customization); then a Wikipedia link (the
        /// richer record); then more resolved credits; then the lowest id as a stable
        /// tiebreak. Deleting a loser unlinks (not destroys) its MediaCredit rows via the
        /// existing DeleteBehavior.SetNull FK -- run reprocess-orphaned-credits afterward to
        /// re-resolve them onto the surviving record. Admin only: maintenance operation, not
        /// something a regular user triggers.
        /// </summary>
        [HttpPost("dedupe-tmdb-duplicates")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DedupeTmdbDuplicates(CancellationToken ct)
        {
            var peopleTypeId = await GetPeopleMediaTypeIdAsync(ct);
            if (peopleTypeId is null)
                return Ok(ApiResponse<object>.Ok(new { groupsProcessed = 0, deleted = 0 }));

            // Load everything needed to make every group's winner decision up front, in bulk --
            // 39,000+ per-group queries would make this endpoint take hours instead of minutes.
            var peopleIds = await _context.MediaItems
                .Where(m => m.MediaTypeId == peopleTypeId.Value)
                .Select(m => m.Id)
                .ToListAsync(ct);
            var peopleIdSet = peopleIds.ToHashSet();

            var tmdbExtIds = await _context.MediaExternalIds
                .Where(e => e.Source == "tmdb")
                .Select(e => new { e.MediaItemId, e.ExternalId })
                .ToListAsync(ct);

            var byNumericId = new Dictionary<string, HashSet<int>>();
            foreach (var e in tmdbExtIds)
            {
                if (!peopleIdSet.Contains(e.MediaItemId)) continue;
                var m = TmdbPersonExternalIdRe.Match(e.ExternalId);
                if (!m.Success) continue;
                var numId = m.Groups[1].Value;
                if (!byNumericId.TryGetValue(numId, out var set)) byNumericId[numId] = set = [];
                set.Add(e.MediaItemId);
            }

            var dupeGroups = byNumericId.Values.Where(set => set.Count > 1).ToList();
            if (dupeGroups.Count == 0)
                return Ok(ApiResponse<object>.Ok(new { groupsProcessed = 0, deleted = 0 }));

            var allDupeItemIds = dupeGroups.SelectMany(s => s).Distinct().ToHashSet();

            var itemsWithOverride = (await _context.MediaItems
                .Where(m => allDupeItemIds.Contains(m.Id) && m.MetadataJson != null && m.MetadataJson.Contains("\"_overrides\""))
                .Select(m => m.Id)
                .ToListAsync(ct)).ToHashSet();

            var itemsWithWikipedia = (await _context.MediaExternalIds
                .Where(e => e.Source == "wikipedia" && allDupeItemIds.Contains(e.MediaItemId))
                .Select(e => e.MediaItemId)
                .ToListAsync(ct)).ToHashSet();

            var creditCounts = (await _context.MediaCredits
                .Where(c => c.PersonMediaItemId != null && allDupeItemIds.Contains(c.PersonMediaItemId.Value))
                .GroupBy(c => c.PersonMediaItemId!.Value)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToListAsync(ct)).ToDictionary(x => x.Id, x => x.Count);

            int groupsProcessed = 0, deleted = 0;

            foreach (var group in dupeGroups)
            {
                ct.ThrowIfCancellationRequested();

                var ordered = group
                    .OrderByDescending(id => itemsWithOverride.Contains(id))
                    .ThenByDescending(id => itemsWithWikipedia.Contains(id))
                    .ThenByDescending(id => creditCounts.GetValueOrDefault(id, 0))
                    .ThenBy(id => id)
                    .ToList();

                var loserIds = ordered.Skip(1).ToList();
                var losers = await _context.MediaItems.Where(m => loserIds.Contains(m.Id)).ToListAsync(ct);
                _context.MediaItems.RemoveRange(losers);
                deleted += losers.Count;
                groupsProcessed++;

                if (groupsProcessed % 200 == 0)
                {
                    await _context.SaveChangesAsync(ct);
                    // Same reason ReprocessOrphanedCredits and PluginHostService's own backfill
                    // clear periodically: an EF change tracker that never resets across 39,000+
                    // groups in one request keeps accumulating every entity it's ever seen,
                    // making each subsequent SaveChangesAsync progressively slower.
                    _context.ChangeTracker.Clear();
                }
            }

            await _context.SaveChangesAsync(ct);
            return Ok(ApiResponse<object>.Ok(new { groupsProcessed, deleted }));
        }
    }
}
