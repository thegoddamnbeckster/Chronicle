using Chronicle.API.DTOs;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/duplicates")]
[Authorize(Roles = "Admin")]
public class DuplicatesController(
    ChronicleDbContext db,
    DuplicateCandidateScanService scanner) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetCandidates(
        [FromQuery] string? mediaType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var q = db.MediaItemDuplicateCandidates
            .Include(c => c.ItemA).ThenInclude(m => m!.MediaType)
            .Include(c => c.ItemA).ThenInclude(m => m!.ExternalIds)
            .Include(c => c.ItemB).ThenInclude(m => m!.MediaType)
            .Include(c => c.ItemB).ThenInclude(m => m!.ExternalIds)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(mediaType))
            q = q.Where(c => c.ItemA!.MediaType!.Name == mediaType);

        var total = await q.CountAsync(ct);
        var candidates = await q
            .OrderBy(c => c.DetectedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var data = candidates.Select(c => new
        {
            candidateId = c.Id,
            itemA = new {
                c.ItemA!.Id, c.ItemA.Name, c.ItemA.PosterUrl, c.ItemA.HierarchyLevel,
                c.ItemA.Year, c.ItemA.Overview,
                mediaType   = c.ItemA.MediaType?.Name,
                externalIds = c.ItemA.ExternalIds.Select(e => new { e.Source, e.ExternalId }).ToList(),
            },
            itemB = new {
                c.ItemB!.Id, c.ItemB.Name, c.ItemB.PosterUrl, c.ItemB.HierarchyLevel,
                c.ItemB.Year, c.ItemB.Overview,
                mediaType   = c.ItemB.MediaType?.Name,
                externalIds = c.ItemB.ExternalIds.Select(e => new { e.Source, e.ExternalId }).ToList(),
            },
        }).ToList<object>();

        return Ok(ApiResponse<List<object>>.Ok(data,
            new PaginationInfo(page, pageSize, total)));
    }

    [HttpPost("dismiss")]
    public async Task<IActionResult> Dismiss(
        [FromBody] DismissDuplicateDto dto,
        CancellationToken ct)
    {
        var a = Math.Min(dto.ItemAId, dto.ItemBId);
        var b = Math.Max(dto.ItemAId, dto.ItemBId);

        var exists = await db.MediaItemDuplicateDismissals
            .AnyAsync(d => d.ItemAId == a && d.ItemBId == b, ct);
        if (!exists)
        {
            db.MediaItemDuplicateDismissals.Add(new MediaItemDuplicateDismissal
            {
                ItemAId     = a,
                ItemBId     = b,
                DismissedAt = DateTime.UtcNow,
            });
        }

        // Remove from candidates
        var candidate = await db.MediaItemDuplicateCandidates
            .FirstOrDefaultAsync(c => (c.ItemAId == a && c.ItemBId == b) ||
                                       (c.ItemAId == b && c.ItemBId == a), ct);
        if (candidate is not null) db.MediaItemDuplicateCandidates.Remove(candidate);

        await db.SaveChangesAsync(ct);
        return Ok(ApiResponse<object>.Ok(new { dismissed = true }));
    }

    [HttpPost("scan")]
    public IActionResult TriggerScan()
    {
        _ = Task.Run(() => scanner.ExecuteAsync(CancellationToken.None));
        return Accepted(ApiResponse<object>.Ok(new { message = "Duplicate candidate scan started." }));
    }
}
