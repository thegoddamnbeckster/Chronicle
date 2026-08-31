using Chronicle.API.DTOs;
using Chronicle.Core.Helpers;
using Chronicle.Data;
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

        public PeopleController(ChronicleDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> GetPeople(
            [FromQuery] string? role = null,
            [FromQuery] bool? deceased = null,
            // Jumps straight to the first person (in last-name order) alphabetically >= this
            // value instead of walking forward page-by-page from the start -- per-user request
            // (2026-08-30): "let me jump into a mid point of the list of people as I need to
            // without having to reload the entire list." Backs both the A-Z rail (single
            // letters) and the jump-search box (arbitrary typed text) on the frontend -- both
            // are the exact same "start from here" query, just different input granularity.
            [FromQuery] string? jumpTo = null,
            [FromQuery] int page = 1,
            [FromQuery] int perPage = 60,
            CancellationToken ct = default)
        {
            var peopleTypeId = await GetPeopleMediaTypeIdAsync(ct);
            if (peopleTypeId is null)
                return Ok(ApiResponse<List<PersonListItemDto>>.Ok([], new PaginationInfo(page, perPage, 0)));

            var query = _context.MediaItems.Where(m => m.MediaTypeId == peopleTypeId.Value);

            if (deceased == true) query = query.Where(m => m.DeathDate != null);
            else if (deceased == false) query = query.Where(m => m.DeathDate == null);

            if (!string.IsNullOrWhiteSpace(role))
                query = query.Where(m => _context.MediaCredits.Any(c => c.PersonMediaItemId == m.Id && c.Role == role));

            // Sorted by last name -- per-user request (2026-08-31): "delete the sorting and
            // keep it alphabetical by last name." There's no separate first/last-name column
            // (a person is just a MediaItem with one Name field), and deriving a "last name"
            // from free text (particles like "del"/"van", generational suffixes) isn't
            // reliably expressible as translatable SQL across both supported providers
            // (SQLite/PostgreSQL) -- so the ordering key is computed in memory via
            // PersonNameHelper, the same helper applied to `jumpTo` below so the two compare
            // on the same key space. Only Id+Name is projected for this pass (cheap even for a
            // large catalog); the current page's full rows are fetched separately below.
            var idsAndNames = await query
                .Select(m => new { m.Id, m.Name })
                .ToListAsync(ct);

            var ordered = idsAndNames
                .Select(m => new { m.Id, m.Name, Key = PersonNameHelper.ToLastNameFirstSortKey(m.Name) })
                .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Id)
                .ToList();

            if (!string.IsNullOrWhiteSpace(jumpTo))
            {
                var target = PersonNameHelper.ToLastNameFirstSortKey(jumpTo);
                ordered = ordered
                    .SkipWhile(m => string.Compare(m.Key, target, StringComparison.OrdinalIgnoreCase) < 0)
                    .ToList();
                page = 1; // a jump always starts a fresh window at the target, never mid-page
            }

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
    }
}
