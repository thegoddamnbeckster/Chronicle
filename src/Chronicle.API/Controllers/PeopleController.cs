using Chronicle.API.DTOs;
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
            [FromQuery] string? sort = "name",
            [FromQuery] string? role = null,
            [FromQuery] bool? deceased = null,
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

            query = sort switch
            {
                // Nulls-last: an unknown birth date shouldn't sort as "earliest ever born".
                "birthDate" => query.OrderBy(m => m.BirthDate == null).ThenBy(m => m.BirthDate),
                "createdAt" => query.OrderByDescending(m => m.CreatedAt),
                _           => query.OrderBy(m => m.Name),
            };

            var total = await query.CountAsync(ct);
            var items = await query.Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);

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
